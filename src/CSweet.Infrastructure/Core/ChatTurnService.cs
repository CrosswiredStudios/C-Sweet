using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ChatTurnService(CSweetDbContext db) : IChatTurnService
{
    private static readonly TimeSpan InitialLeaseDuration = TimeSpan.FromMinutes(3);
    private static readonly HashSet<ChatTurnStatus> ActiveStatuses =
    [ChatTurnStatus.Queued, ChatTurnStatus.RecallingMemory, ChatTurnStatus.Dispatching, ChatTurnStatus.Running, ChatTurnStatus.FinalizingMemory];
    private static readonly HashSet<ChatTurnStatus> TerminalStatuses =
    [ChatTurnStatus.Completed, ChatTurnStatus.CompletedWithWarnings, ChatTurnStatus.Failed, ChatTurnStatus.Cancelled];

    public async Task<ChatTurnStartResponse?> StartAsync(Guid organizationId, Guid conversationId, string message, Guid? retryOfTurnId = null, CancellationToken cancellationToken = default)
    {
        var target = await db.CoreConversations
            .Where(x => x.Id == conversationId && x.OrganizationId == organizationId)
            .Select(x => x.AgentOrganizationUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!target.HasValue) return null;
        return await StartCoreAsync(organizationId, conversationId, target.Value, message, null, "InApp", null, null, retryOfTurnId, cancellationToken);
    }

    public Task<ChatTurnStartResponse?> StartForAgentAsync(
        Guid organizationId, Guid conversationId, Guid targetAgentOrganizationUserId, string message,
        Guid? senderOrganizationUserId = null, string sourceProvider = "InApp", string? sourceChannelExternalId = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        StartCoreAsync(organizationId, conversationId, targetAgentOrganizationUserId, message, senderOrganizationUserId,
            sourceProvider, sourceChannelExternalId, idempotencyKey, null, cancellationToken);

    private async Task<ChatTurnStartResponse?> StartCoreAsync(
        Guid organizationId, Guid conversationId, Guid targetAgentOrganizationUserId, string message,
        Guid? senderOrganizationUserId, string sourceProvider, string? sourceChannelExternalId,
        string? idempotencyKey, Guid? retryOfTurnId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var conversation = await db.CoreConversations.SingleOrDefaultAsync(
            x => x.Id == conversationId && x.OrganizationId == organizationId, cancellationToken);
        if (conversation is null) return null;
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.Id == targetAgentOrganizationUserId &&
            x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Agent && x.IsActive, cancellationToken)) return null;
        if (idempotencyKey is not null && await db.CoreConversationMessages.AnyAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken)) return null;
        if (await db.ChatTurns.AnyAsync(x => x.ConversationId == conversationId &&
            x.TargetAgentOrganizationUserId == targetAgentOrganizationUserId && ActiveStatuses.Contains(x.Status), cancellationToken))
            throw new InvalidOperationException("This employee already has an active turn in the conversation.");

        var now = DateTimeOffset.UtcNow;
        var turnId = Guid.NewGuid();
        var userMessage = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, ChatTurnId = turnId,
            Role = ConversationRole.User, Content = message.Trim(), CreatedAt = now,
            SenderOrganizationUserId = senderOrganizationUserId,
            CorrelationId = Guid.NewGuid(), DeliveryIntent = CommunicationDeliveryIntent.RequestResponse,
            SourceProvider = sourceProvider, SourceChannelExternalId = sourceChannelExternalId,
            IdempotencyKey = idempotencyKey
        };
        var turn = new ChatTurn
        {
            Id = turnId, OrganizationId = organizationId, ConversationId = conversationId,
            TargetAgentOrganizationUserId = targetAgentOrganizationUserId,
            UserMessageId = userMessage.Id, RetryOfTurnId = retryOfTurnId, Status = ChatTurnStatus.Queued,
            CreatedAt = now, UpdatedAt = now, LastActivityAt = now
        };
        conversation.UpdatedAt = now;
        conversation.Title ??= userMessage.Content.Length <= 80 ? userMessage.Content : userMessage.Content[..80];
        db.CoreConversationMessages.Add(userMessage);
        db.ChatTurns.Add(turn);
        db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem
        {
            Id = Guid.NewGuid(), ConversationMessageId = userMessage.Id, Status = MemoryCaptureStatus.Pending,
            CreatedAt = now, NextAttemptAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new ChatTurnStartResponse(ToResponse(turn), userMessage.ToResponse());
    }

    public async Task<ChatTurnResponse?> GetAsync(Guid organizationId, Guid turnId, CancellationToken cancellationToken = default) =>
        await db.ChatTurns.Where(x => x.Id == turnId && x.OrganizationId == organizationId)
            .Select(x => ToResponse(x)).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ChatTurnResponse>> ListForConversationAsync(
        Guid organizationId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await db.ChatTurns
            .Where(x => x.OrganizationId == organizationId && x.ConversationId == conversationId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => ToResponse(x))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ChatTurnTraceEventResponse>> ListEventsAsync(Guid organizationId, Guid turnId, long afterSequence = -1, CancellationToken cancellationToken = default) =>
        await db.ChatTurnTraceEvents.Where(x => x.ChatTurnId == turnId && x.ChatTurn!.OrganizationId == organizationId && x.Sequence > afterSequence)
            .OrderBy(x => x.Sequence).Select(x => ToResponse(x)).ToListAsync(cancellationToken);

    public async Task<bool> CancelAsync(Guid organizationId, Guid turnId, CancellationToken cancellationToken = default)
    {
        var turn = await db.ChatTurns.SingleOrDefaultAsync(x => x.Id == turnId && x.OrganizationId == organizationId, cancellationToken);
        if (turn is null || !ActiveStatuses.Contains(turn.Status)) return false;
        turn.Status = ChatTurnStatus.Cancelled;
        turn.CompletedAt = turn.UpdatedAt = DateTimeOffset.UtcNow;
        turn.LeaseOwner = null; turn.LeaseUntil = null;
        await CancelPendingDecisionsAsync(turnId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ChatTurnStartResponse?> RetryAsync(Guid organizationId, Guid turnId, CancellationToken cancellationToken = default)
    {
        var original = await db.ChatTurns.Include(x => x.UserMessage)
            .SingleOrDefaultAsync(x => x.Id == turnId && x.OrganizationId == organizationId, cancellationToken);
        if (original is null || original.Status is not (ChatTurnStatus.Failed or ChatTurnStatus.Cancelled or ChatTurnStatus.CompletedWithWarnings)) return null;
        return await StartCoreAsync(organizationId, original.ConversationId, original.TargetAgentOrganizationUserId,
            original.UserMessage!.Content, original.UserMessage.SenderOrganizationUserId,
            original.UserMessage.SourceProvider, original.UserMessage.SourceChannelExternalId,
            null, original.Id, cancellationToken);
    }

    public async Task<Guid?> ClaimNextAsync(string leaseOwner, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = await db.ChatTurns.Where(x => x.Status == ChatTurnStatus.Queued ||
                (ActiveStatuses.Contains(x.Status) && x.LeaseUntil < now))
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (turn is null) return null;
        turn.LeaseOwner = leaseOwner;
        var recovering = turn.Status != ChatTurnStatus.Queued;
        turn.LeaseUntil = now.Add(InitialLeaseDuration);
        turn.Attempt++;
        if (recovering)
        {
            turn.PartialResponse = string.Empty;
            turn.FirstOutputAt = null;
            turn.ErrorCode = null;
            turn.ErrorMessage = null;
        }
        turn.Status = ChatTurnStatus.RecallingMemory;
        turn.StartedAt ??= now;
        turn.LastActivityAt = turn.UpdatedAt = now;
        turn.LeaseUntil = now.Add(InitialLeaseDuration);
        await db.SaveChangesAsync(cancellationToken);
        return turn.Id;
    }

    public async Task<ChatTurnTraceEventResponse> TraceAsync(Guid turnId, string category, string eventType, string status, string title, string? summary = null, object? details = null, string sensitivity = "Internal", long? durationMs = null, CancellationToken cancellationToken = default)
    {
        var turn = await db.ChatTurns.SingleAsync(x => x.Id == turnId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var traceEvent = new ChatTurnTraceEvent
        {
            Id = Guid.NewGuid(), ChatTurnId = turnId, Sequence = turn.NextTraceSequence++, Category = category,
            EventType = eventType, Status = status, Title = title, Summary = summary,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details), Sensitivity = sensitivity,
            DurationMs = durationMs, OccurredAt = now
        };
        turn.LastActivityAt = turn.UpdatedAt = now;
        db.ChatTurnTraceEvents.Add(traceEvent);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(traceEvent);
    }

    public async Task SetStatusAsync(Guid turnId, string status, string? errorCode = null, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var turn = await db.ChatTurns.SingleAsync(x => x.Id == turnId, cancellationToken);
        var nextStatus = Enum.Parse<ChatTurnStatus>(status, true);
        if (TerminalStatuses.Contains(turn.Status) && turn.Status != nextStatus)
            return;
        turn.Status = nextStatus;
        turn.ErrorCode = errorCode; turn.ErrorMessage = errorMessage;
        var now = DateTimeOffset.UtcNow;
        turn.UpdatedAt = now;
        turn.LastActivityAt = now;
        if (turn.Status is ChatTurnStatus.Failed or ChatTurnStatus.Cancelled)
            await CancelPendingDecisionsAsync(turnId, cancellationToken);
        if (turn.Status is ChatTurnStatus.Failed or ChatTurnStatus.Cancelled) turn.CompletedAt = turn.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CancelPendingDecisionsAsync(Guid turnId, CancellationToken cancellationToken)
    {
        var pending = await db.ExecutiveDecisions.Where(x => x.ChatTurnId == turnId &&
            x.Status == ExecutiveDecisionStatus.Pending).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var decision in pending)
        {
            decision.Status = ExecutiveDecisionStatus.Cancelled;
            decision.UpdatedAt = now;
        }
    }

    public async Task AppendOutputAsync(Guid turnId, string delta, CancellationToken cancellationToken = default)
    {
        var turn = await db.ChatTurns.SingleAsync(x => x.Id == turnId, cancellationToken);
        if (TerminalStatuses.Contains(turn.Status))
            return;
        turn.PartialResponse += delta;
        var now = DateTimeOffset.UtcNow;
        turn.FirstOutputAt ??= now;
        turn.LastActivityAt = now;
        turn.UpdatedAt = now;
        turn.LeaseUntil = now.AddMinutes(1);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceOutputAsync(Guid turnId, string content, CancellationToken cancellationToken = default)
    {
        var turn = await db.ChatTurns.SingleAsync(x => x.Id == turnId, cancellationToken);
        if (TerminalStatuses.Contains(turn.Status))
            return;
        turn.PartialResponse = content;
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(content)) turn.FirstOutputAt ??= now;
        turn.LastActivityAt = turn.UpdatedAt = now;
        turn.LeaseUntil = now.AddMinutes(1);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(Guid turnId, Guid assistantMessageId, bool memoryWarning, CancellationToken cancellationToken = default)
    {
        var turn = await db.ChatTurns.SingleAsync(x => x.Id == turnId, cancellationToken);
        if (TerminalStatuses.Contains(turn.Status))
            return;
        var now = DateTimeOffset.UtcNow;
        turn.AssistantMessageId = assistantMessageId;
        turn.Status = memoryWarning ? ChatTurnStatus.CompletedWithWarnings : ChatTurnStatus.Completed;
        turn.ResponseReadyAt = turn.CompletedAt = turn.UpdatedAt = now;
        turn.LeaseOwner = null; turn.LeaseUntil = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ChatTurnResponse ToResponse(ChatTurn x) => new(
        x.Id, x.OrganizationId, x.ConversationId, x.UserMessageId, x.AssistantMessageId,
        x.Status.ToString(), x.Attempt, x.PartialResponse, x.ErrorCode, x.ErrorMessage,
        x.CreatedAt, x.StartedAt, x.FirstOutputAt, x.ResponseReadyAt, x.CompletedAt, x.NextTraceSequence - 1)
    {
        TargetAgentOrganizationUserId = x.TargetAgentOrganizationUserId
    };

    private static ChatTurnTraceEventResponse ToResponse(ChatTurnTraceEvent x)
    {
        JsonElement? details = null;
        if (!string.IsNullOrWhiteSpace(x.DetailsJson)) details = JsonSerializer.Deserialize<JsonElement>(x.DetailsJson);
        return new(x.Id, x.ChatTurnId, x.Sequence, x.Category, x.EventType, x.Status, x.Title,
            x.Summary, details, x.Sensitivity, x.DurationMs, x.OccurredAt);
    }
}
