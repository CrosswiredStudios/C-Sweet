using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class UserActionService(
    CSweetDbContext db,
    IEnumerable<IUserActionWorkflowResolver> resolvers) : IUserActionService
{
    public async Task<SuggestedUserActionResponse> SuggestAsync(
        Guid organizationId,
        Guid originatingInstallationId,
        SuggestUserActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.MessageId.HasValue == request.ChatTurnId.HasValue)
            throw new ArgumentException("Exactly one messageId or chatTurnId is required.");

        var key = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var existing = await db.SuggestedUserActions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OriginatingInstallationId == originatingInstallationId && x.IdempotencyKey == key,
            cancellationToken);
        if (existing is not null) return ToResponse(existing);

        var actorId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.AgentInstallationId == originatingInstallationId &&
                        x.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The installation is not assigned to an active employee.");

        Guid conversationId;
        Guid? targetChatTurnId;
        var materializeImmediately = request.MessageId.HasValue;
        if (request.MessageId.HasValue)
        {
            var target = await db.CoreConversationMessages.AsNoTracking()
                .Where(x => x.Id == request.MessageId &&
                            x.SenderOrganizationUserId == actorId &&
                            x.Conversation!.OrganizationId == organizationId)
                .Select(x => new { x.ConversationId, x.ChatTurnId })
                .SingleOrDefaultAsync(cancellationToken);
            conversationId = target?.ConversationId ?? Guid.Empty;
            targetChatTurnId = target?.ChatTurnId;
        }
        else
        {
            var target = await db.ChatTurns.AsNoTracking()
                .Where(x => x.Id == request.ChatTurnId &&
                            x.OrganizationId == organizationId &&
                            x.TargetAgentOrganizationUserId == actorId)
                .Select(x => new { x.ConversationId, x.AssistantMessageId })
                .SingleOrDefaultAsync(cancellationToken);
            conversationId = target?.ConversationId ?? Guid.Empty;
            targetChatTurnId = request.ChatTurnId;
            materializeImmediately = target?.AssistantMessageId.HasValue == true;
        }
        if (conversationId == Guid.Empty)
            throw new UnauthorizedAccessException("The target message or chat turn does not belong to this installation.");

        var workflowType = Required(request.WorkflowType, 160, nameof(request.WorkflowType));
        var resolver = resolvers.SingleOrDefault(x =>
            string.Equals(x.WorkflowType, workflowType, StringComparison.Ordinal))
            ?? throw new ArgumentException("The requested workflow type is not registered.");
        var resolution = resolver.Resolve(organizationId, originatingInstallationId, request.Parameters);

        var isHiringWorkflow = string.Equals(
            workflowType,
            SuggestedUserActionWorkflows.BrowseHiringMarketplace,
            StringComparison.Ordinal);
        var hiringRecommendationId = isHiringWorkflow
            ? SuggestedUserActionParameters.ReadHiringRecommendationId(resolution.NormalizedParametersJson)
            : null;
        var hiringRole = isHiringWorkflow
            ? SuggestedUserActionParameters.ReadHiringRole(resolution.NormalizedParametersJson)
            : null;

        // Only one actionable hiring suggestion per conversation: a request for a recommendation that
        // is already surfaced returns the pending action instead of stacking duplicate widgets.
        if (isHiringWorkflow)
        {
            var pending = await db.SuggestedUserActions.AsNoTracking()
                .Where(x => x.ConversationId == conversationId &&
                            x.WorkflowType == workflowType &&
                            x.Status == SuggestedUserActionStatuses.Pending)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken);
            var duplicate = pending.FirstOrDefault(x => SuggestedUserActionParameters.MatchesHiringTarget(
                x.ParametersJson, hiringRecommendationId, hiringRole));
            if (duplicate is not null) return ToResponse(duplicate);
        }

        var now = DateTimeOffset.UtcNow;
        var label = Required(request.Label, 120, nameof(request.Label));
        var description = Clean(request.Description, 500, nameof(request.Description));
        var action = new SuggestedUserAction
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            OriginatingInstallationId = originatingInstallationId,
            ConversationId = conversationId,
            ConversationMessageId = null,
            ChatTurnId = materializeImmediately ? null : request.ChatTurnId,
            WorkflowType = workflowType,
            Label = label,
            Description = description,
            ParametersJson = resolution.NormalizedParametersJson,
            NavigationUri = resolution.NavigationUri,
            IdempotencyKey = key,
            Status = SuggestedUserActionStatuses.Pending,
            CreatedAt = now
        };
        db.SuggestedUserActions.Add(action);
        if (materializeImmediately)
        {
            await SuggestedUserActionMaterializer.MaterializeAsync(
                db,
                action,
                request.MessageId ?? request.ChatTurnId!.Value,
                targetChatTurnId,
                now,
                cancellationToken);
        }
        if (isHiringWorkflow)
            await SupersedeEarlierSuggestionsAsync(action, hiringRole, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(action);
    }

    /// <summary>
    /// Keeps one actionable hiring suggestion per conversation: earlier pending (or already cancelled)
    /// suggestions become Superseded with lineage to the replacement. Rows created by the same source
    /// (same chat turn or same message) belong to one multi-role carousel and are preserved.
    /// </summary>
    private async Task SupersedeEarlierSuggestionsAsync(
        SuggestedUserAction replacement,
        string? replacementRole,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var earlier = await db.SuggestedUserActions
            .Where(x => x.ConversationId == replacement.ConversationId &&
                        x.WorkflowType == replacement.WorkflowType &&
                        x.Id != replacement.Id &&
                        (x.Status == SuggestedUserActionStatuses.Pending ||
                         x.Status == SuggestedUserActionStatuses.Cancelled))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var candidate in earlier)
        {
            if ((replacement.ChatTurnId.HasValue && candidate.ChatTurnId == replacement.ChatTurnId) ||
                (replacement.ConversationMessageId.HasValue &&
                 candidate.ConversationMessageId == replacement.ConversationMessageId))
                continue;
            candidate.Status = SuggestedUserActionStatuses.Superseded;
            candidate.SupersededAt = now;
            candidate.SupersededByActionId = replacement.Id;
            candidate.SupersededByRole = replacementRole;
            await SuggestedUserActionMaterializer.QueueStatusChangedEventAsync(
                db,
                candidate,
                SuggestedUserActionEvents.Superseded,
                now,
                new
                {
                    actionId = candidate.Id,
                    supersedingActionId = replacement.Id,
                    messageId = candidate.ConversationMessageId,
                    workflowType = replacement.WorkflowType,
                    role = replacementRole
                },
                cancellationToken);
        }
    }

    private static SuggestedUserActionResponse ToResponse(SuggestedUserAction action) =>
        new(action.Id, action.WorkflowType, action.Label, action.Description, action.NavigationUri,
            action.Status, action.CreatedAt)
        {
            HiringRecommendationId = SuggestedUserActionParameters.ReadHiringRecommendationId(action.ParametersJson),
            HiringRole = SuggestedUserActionParameters.ReadHiringRole(action.ParametersJson),
            ResultOrganizationUserId = action.ResultOrganizationUserId,
            CompletedAt = action.CompletedAt,
            SupersededAt = action.SupersededAt,
            SupersededByActionId = action.SupersededByActionId,
            SupersededByRole = action.SupersededByRole
        };

    private static string Required(string? value, int maximum, string name)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length > maximum)
            throw new ArgumentException($"{name} is required and cannot exceed {maximum} characters.");
        return cleaned;
    }

    private static string? Clean(string? value, int maximum, string name)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (cleaned?.Length > maximum) throw new ArgumentException($"{name} cannot exceed {maximum} characters.");
        return cleaned;
    }
}

internal static class SuggestedUserActionParameters
{
    public static string? ReadHiringRole(string parametersJson)
    {
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            if (!document.RootElement.TryGetProperty("role", out var role) ||
                role.ValueKind != JsonValueKind.String)
                return null;
            var value = role.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Guid? ReadHiringRecommendationId(string parametersJson)
    {
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            return document.RootElement.TryGetProperty("recommendationId", out var recommendation) &&
                   recommendation.ValueKind == JsonValueKind.String &&
                   recommendation.TryGetGuid(out var recommendationId)
                ? recommendationId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Matches an existing suggestion against the incoming request: by recommendation when either side
    /// carries one, otherwise by case-insensitive role text.
    /// </summary>
    public static bool MatchesHiringTarget(string parametersJson, Guid? recommendationId, string? role)
    {
        var existingRecommendationId = ReadHiringRecommendationId(parametersJson);
        if (recommendationId.HasValue || existingRecommendationId.HasValue)
            return recommendationId.HasValue && existingRecommendationId == recommendationId;
        var existingRole = ReadHiringRole(parametersJson);
        return !string.IsNullOrWhiteSpace(existingRole) &&
               string.Equals(existingRole, role, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class SuggestedUserActionMaterializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string SystemActionSourceProvider = "SystemAction";

    public static async Task MaterializeAsync(
        CSweetDbContext db,
        SuggestedUserAction action,
        Guid causationId,
        Guid? sourceChatTurnId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (action.ConversationMessageId.HasValue) return;
        var systemMessage = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = action.ConversationId,
            Role = ConversationRole.Assistant,
            Content = action.Description ?? action.Label,
            CreatedAt = now,
            ChatTurnId = sourceChatTurnId,
            SenderOrganizationUserId = null,
            CorrelationId = Guid.NewGuid(),
            CausationId = causationId,
            DeliveryIntent = CommunicationDeliveryIntent.Inform,
            SourceProvider = SystemActionSourceProvider,
            IdempotencyKey = $"suggested-action:{action.OriginatingInstallationId:N}:{action.IdempotencyKey}"
        };
        action.ConversationMessageId = systemMessage.Id;
        action.ChatTurnId = null;
        db.CoreConversationMessages.Add(systemMessage);
        var conversation = await db.CoreConversations.SingleAsync(
            x => x.Id == action.ConversationId,
            cancellationToken);
        conversation.UpdatedAt = now;
        await QueueStatusChangedEventAsync(
            db,
            action,
            SuggestedUserActionEvents.Created,
            now,
            new
            {
                action.Id,
                action.WorkflowType,
                MessageId = systemMessage.Id
            },
            cancellationToken);
    }

    /// <summary>
    /// Queues the versioned realtime envelope that tells connected clients a suggestion changed state
    /// (created, superseded, or cancelled). Communications pages refresh on any com.csweet.communication.* event.
    /// </summary>
    public static async Task QueueStatusChangedEventAsync(
        CSweetDbContext db,
        SuggestedUserAction action,
        string eventType,
        DateTimeOffset now,
        object payload,
        CancellationToken cancellationToken)
    {
        var recipients = await db.ConversationParticipants.AsNoTracking()
            .Where(x => x.ConversationId == action.ConversationId && x.LeftAt == null)
            .Select(x => x.OrganizationUserId)
            .ToListAsync(cancellationToken);
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = action.OrganizationId,
            RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipients, JsonOptions),
            ChatId = action.ConversationId,
            EventType = eventType,
            Subject = $"organizations/{action.OrganizationId:D}/communications/chats/{action.ConversationId:D}/actions/{action.Id:D}",
            DataJson = JsonSerializer.Serialize(payload, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }
}

public sealed class HiringMarketplaceUserActionWorkflowResolver : IUserActionWorkflowResolver
{
    public string WorkflowType => SuggestedUserActionWorkflows.BrowseHiringMarketplace;

    public UserActionWorkflowResolution Resolve(Guid organizationId, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("role", out var roleElement) ||
            roleElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("The hiring marketplace workflow requires a role.");
        var role = roleElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(role) || role.Length > 160)
            throw new ArgumentException("The role is required and cannot exceed 160 characters.");
        Guid? recommendationId = null;
        if (parameters.TryGetProperty("recommendationId", out var recommendationElement) &&
            recommendationElement.ValueKind is not JsonValueKind.Null)
        {
            if (recommendationElement.ValueKind != JsonValueKind.String ||
                !recommendationElement.TryGetGuid(out var parsedRecommendationId) ||
                parsedRecommendationId == Guid.Empty)
                throw new ArgumentException("The hiring recommendation identifier is invalid.");
            recommendationId = parsedRecommendationId;
        }
        var normalized = JsonSerializer.Serialize(new { role, recommendationId });
        var recommendationQuery = recommendationId.HasValue
            ? $"&recommendationId={recommendationId.Value:D}"
            : string.Empty;
        return new(
            $"/organizations/{organizationId:D}/marketplace?role={Uri.EscapeDataString(role)}{recommendationQuery}",
            normalized);
    }
}
