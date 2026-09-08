using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed partial class ExecutiveDecisionService(
    CSweetDbContext db,
    IChatTurnService turns,
    IAuditEventWriter? audit = null,
    IAgentConfigurationService? configurations = null,
    CSweet.Infrastructure.Setup.AgentWorkRouter? router = null) : IExecutiveDecisionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExecutiveDecisionCardResponse> CreateAsync(
        CreateExecutiveDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = Required(command.IdempotencyKey, 160, nameof(command.IdempotencyKey));
        var existing = await db.ExecutiveDecisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestingInstallationId == command.RequestingInstallationId &&
                x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return ToCard(existing);

        var prompt = Required(command.Prompt, 2048, nameof(command.Prompt));
        if (command.Options.Count is < 2 or > 4)
            throw new ArgumentException("A decision must contain between two and four mutually exclusive options.");

        var options = command.Options.Select(option => new StoredOption(
                Required(option.Id, 80, "option.id"),
                Required(option.Label, 160, "option.label"),
                Clean(option.Description, 500)))
            .ToList();
        if (options.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != options.Count)
            throw new ArgumentException("Decision option IDs must be unique.");
        var recommendedOptionId = Required(command.RecommendedOptionId, 80, nameof(command.RecommendedOptionId));
        if (!options.Any(x => x.Id == recommendedOptionId))
            throw new ArgumentException("The recommended option must identify one of the supplied options.");

        if (command.ChatTurnId.HasValue == command.ConversationMessageId.HasValue)
            throw new ArgumentException("Attach the decision to exactly one chat turn or conversation message.");
        if (command.ChatTurnId.HasValue)
        {
            var turnIdentity = await (from turn in db.ChatTurns.AsNoTracking()
                join agent in db.CoreOrganizationUsers.AsNoTracking()
                    on turn.TargetAgentOrganizationUserId equals agent.Id
                where turn.Id == command.ChatTurnId && turn.OrganizationId == command.OrganizationId &&
                      turn.ConversationId == command.ConversationId
                select agent.AgentInstallationId)
                .SingleOrDefaultAsync(cancellationToken);
            if (turnIdentity != command.RequestingInstallationId)
                throw new InvalidOperationException("The decision must be attached to the requesting agent's active chat turn.");
        }
        else
        {
            var messageIdentity = await (from message in db.CoreConversationMessages.AsNoTracking()
                join conversation in db.CoreConversations.AsNoTracking()
                    on message.ConversationId equals conversation.Id
                join agent in db.CoreOrganizationUsers.AsNoTracking()
                    on message.SenderOrganizationUserId equals agent.Id
                where message.Id == command.ConversationMessageId &&
                      message.ConversationId == command.ConversationId &&
                      conversation.OrganizationId == command.OrganizationId &&
                      message.Role == ConversationRole.Assistant
                select agent.AgentInstallationId)
                .SingleOrDefaultAsync(cancellationToken);
            if (messageIdentity != command.RequestingInstallationId)
                throw new InvalidOperationException("The decision must be attached to a message sent by the requesting agent.");
        }

        if (command.ConfigurationChange is { } change)
        {
            var view = await ReadConfigurationChoiceAsync(command.OrganizationId, command.RequestingInstallationId, change, cancellationToken);
            var field = view.Fields.Single(x => x.Key == change.Key);
            var currentLabel = field.Options!.Single(x => x.Value == change.CurrentValue).Label;
            var proposedLabel = field.Options!.Single(x => x.Value == change.ProposedValue).Label;
            prompt = $"Current {field.Label}: {currentLabel}. Switch to {proposedLabel}?";
            options = [new("apply", $"Switch to {proposedLabel}", null), new("leave-unchanged", "Leave unchanged", $"Keep {currentLabel}.")];
            recommendedOptionId = "apply";
        }

        var now = DateTimeOffset.UtcNow;
        var pending = await db.ExecutiveDecisions
            .Where(x => x.ConversationId == command.ConversationId &&
                x.RequestingInstallationId == command.RequestingInstallationId &&
                x.Status == ExecutiveDecisionStatus.Pending)
            .ToListAsync(cancellationToken);
        var decision = new ExecutiveDecision
        {
            Id = Guid.NewGuid(), OrganizationId = command.OrganizationId, ConversationId = command.ConversationId,
            ChatTurnId = command.ChatTurnId, ConversationMessageId = command.ConversationMessageId,
            RequestingInstallationId = command.RequestingInstallationId,
            Prompt = prompt, OptionsJson = JsonSerializer.Serialize(new StoredDecisionOptions(options, command.ConfigurationChange), JsonOptions),
            RecommendedOptionId = recommendedOptionId, IdempotencyKey = idempotencyKey,
            Status = ExecutiveDecisionStatus.Pending, CreatedAt = now, UpdatedAt = now
        };
        foreach (var previous in pending)
        {
            previous.Status = ExecutiveDecisionStatus.Superseded;
            previous.SupersededByDecisionId = decision.Id;
            previous.UpdatedAt = now;
        }
        db.ExecutiveDecisions.Add(decision);
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null)
        {
            var requestingAgent = await db.CoreOrganizationUsers.AsNoTracking()
                .SingleAsync(x => x.OrganizationId == command.OrganizationId &&
                    x.AgentInstallationId == command.RequestingInstallationId, cancellationToken);
            var promptBytes = Encoding.UTF8.GetBytes(prompt);
            await audit.AppendAsync(new AuditEventWriteRequest(
                "communication.user-input.requested",
                "Communication",
                "Outbound",
                "Accepted",
                command.OrganizationId,
                "ExecutiveDecision",
                decision.Id,
                $"{requestingAgent.DisplayName} requested input from the user.",
                JsonSerializer.Serialize(new
                {
                    command.ConversationId,
                    command.ChatTurnId,
                    command.ConversationMessageId,
                    optionCount = options.Count,
                    recommendedOptionId,
                    promptBytes = promptBytes.Length,
                    promptSha256 = Convert.ToHexString(SHA256.HashData(promptBytes))
                }, JsonOptions),
                ExternalRequestId: decision.Id.ToString("D"),
                CorrelationId: (command.ChatTurnId ?? command.ConversationMessageId)!.Value.ToString("D"),
                Actor: new AuditActor(
                    "Agent",
                    true,
                    OrganizationUserId: requestingAgent.Id,
                    DisplayName: requestingAgent.DisplayName,
                    AgentId: requestingAgent.DisplayName,
                    InstallationId: command.RequestingInstallationId),
                Target: new AuditTarget("User", "Conversation participants")),
                cancellationToken);
        }
        return ToCard(decision);
    }

    public async Task<IReadOnlyDictionary<Guid, ExecutiveDecisionCardResponse>> ListForMessagesAsync(
        Guid organizationId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var decisions = await db.ExecutiveDecisions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ConversationId == conversationId &&
                x.Status != ExecutiveDecisionStatus.Cancelled)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return decisions.GroupBy(x => x.ConversationMessageId ?? x.ChatTurnId!.Value)
            .ToDictionary(group => group.Key, group => ToCard(group.First()));
    }

    public async Task<AnswerExecutiveDecisionResponse> AnswerAsync(
        Guid organizationId,
        Guid conversationId,
        Guid decisionId,
        Guid actorOrganizationUserId,
        AnswerExecutiveDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var decision = await db.ExecutiveDecisions
            .SingleOrDefaultAsync(x => x.Id == decisionId && x.OrganizationId == organizationId &&
                x.ConversationId == conversationId, cancellationToken);
        if (decision is null) return Failure("decision_not_found", "The decision was not found.");
        var isMember = await db.ConversationParticipants.AsNoTracking().AnyAsync(x =>
            x.ConversationId == conversationId && x.OrganizationUserId == actorOrganizationUserId && x.LeftAt == null,
            cancellationToken);
        if (!isMember) return Failure("not_authorized", "You are not an active member of this chat.");

        var answerKey = Clean(request.IdempotencyKey, 160);
        if (answerKey is null) return Failure("validation_error", "An idempotency key is required.");
        if (decision.Status == ExecutiveDecisionStatus.Answered)
        {
            if (!string.Equals(decision.AnswerIdempotencyKey, answerKey, StringComparison.Ordinal))
                return Failure("decision_already_answered", "This decision already has an immutable answer.", ToCard(decision));
            var existingTurn = decision.NextChatTurnId.HasValue
                ? await turns.GetAsync(organizationId, decision.NextChatTurnId.Value, cancellationToken)
                : null;
            return new(true, null, "The answer was already submitted.", ToCard(decision), existingTurn);
        }
        if (decision.Status != ExecutiveDecisionStatus.Pending)
            return Failure("decision_not_pending", "This decision is no longer pending.", ToCard(decision));

        var storedOptions = ReadDecisionOptions(decision.OptionsJson);
        var options = storedOptions.Options;
        var optionId = Clean(request.OptionId, 80);
        var freeText = Clean(request.SomethingElse, 4000);
        if (storedOptions.ConfigurationChange is not null && freeText is not null)
            return Failure("validation_error", "Choose Switch or Leave unchanged for this configuration decision.");
        if ((optionId is null) == (freeText is null))
            return Failure("validation_error", "Choose one option or provide a Something else response.");
        var selected = optionId is null ? null : options.SingleOrDefault(x => x.Id == optionId);
        if (optionId is not null && selected is null)
            return Failure("validation_error", "The selected option is not valid for this decision.");

        if (storedOptions.ConfigurationChange is { } configurationChange)
            return await AnswerConfigurationChoiceAsync(decision, configurationChange, selected!, actorOrganizationUserId, answerKey, cancellationToken);

        var targetAgentId = await db.CoreConversations.AsNoTracking()
            .Where(x => x.Id == conversationId && x.OrganizationId == organizationId && x.ArchivedAt == null)
            .Select(x => x.AgentOrganizationUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!targetAgentId.HasValue) return Failure("agent_unavailable", "The agent for this chat is not available.");

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = DateTimeOffset.UtcNow;
        decision.SelectedOptionId = selected?.Id;
        decision.FreeTextAnswer = freeText;
        decision.AnsweredByOrganizationUserId = actorOrganizationUserId;
        decision.AnswerIdempotencyKey = answerKey;
        decision.AnsweredAt = now;
        decision.UpdatedAt = now;
        decision.Status = ExecutiveDecisionStatus.Answered;
        await db.SaveChangesAsync(cancellationToken);

        var answer = selected?.Label ?? freeText!;
        var started = await turns.StartForAgentAsync(organizationId, conversationId, targetAgentId.Value,
            $"Decision: {decision.Prompt}\nAnswer: {answer}", actorOrganizationUserId,
            idempotencyKey: $"executive-decision-answer:{decision.Id:N}", cancellationToken: cancellationToken);
        if (started is null) throw new InvalidOperationException("The decision answer could not start the next chat turn.");
        decision.NextChatTurnId = started.Turn.Id;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        if (audit is not null)
        {
            var responder = await db.CoreOrganizationUsers.AsNoTracking()
                .SingleAsync(x => x.Id == actorOrganizationUserId, cancellationToken);
            var targetAgent = await db.CoreOrganizationUsers.AsNoTracking()
                .SingleAsync(x => x.Id == targetAgentId.Value, cancellationToken);
            var answerBytes = Encoding.UTF8.GetBytes(answer);
            await audit.AppendAsync(new AuditEventWriteRequest(
                "communication.user-input.responded",
                "Communication",
                "Outbound",
                "Delivered",
                organizationId,
                "ExecutiveDecision",
                decision.Id,
                $"{responder.DisplayName} responded to {targetAgent.DisplayName}'s request.",
                JsonSerializer.Serialize(new
                {
                    conversationId,
                    decision.ChatTurnId,
                    decision.ConversationMessageId,
                    nextChatTurnId = started.Turn.Id,
                    selectedOptionId = selected?.Id,
                    usedFreeText = freeText is not null,
                    answerBytes = answerBytes.Length,
                    answerSha256 = Convert.ToHexString(SHA256.HashData(answerBytes))
                }, JsonOptions),
                ExternalRequestId: decision.Id.ToString("D"),
                CorrelationId: started.Turn.Id.ToString("D"),
                Actor: new AuditActor(
                    "Human",
                    true,
                    responder.ApplicationUserId,
                    responder.Id,
                    responder.DisplayName),
                Target: new AuditTarget(
                    "Agent",
                    targetAgent.DisplayName,
                    targetAgent.DisplayName,
                    targetAgent.AgentInstallationId)),
                cancellationToken);
        }
        return new(true, null, "Decision submitted.", ToCard(decision), started.Turn);
    }

    public async Task CancelPendingForTurnAsync(Guid chatTurnId, CancellationToken cancellationToken = default)
    {
        var pending = await db.ExecutiveDecisions.Where(x => x.ChatTurnId == chatTurnId &&
            x.Status == ExecutiveDecisionStatus.Pending).ToListAsync(cancellationToken);
        if (pending.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var decision in pending)
        {
            decision.Status = ExecutiveDecisionStatus.Cancelled;
            decision.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ExecutiveDecisionCardResponse ToCard(ExecutiveDecision decision)
    {
        var options = ReadOptions(decision.OptionsJson)
            .OrderByDescending(x => x.Id == decision.RecommendedOptionId)
            .Select(x => new ExecutiveDecisionOptionResponse(x.Id, x.Label, x.Description,
                x.Id == decision.RecommendedOptionId))
            .ToList();
        return new(decision.Id, decision.Prompt, decision.Status.ToString(), options,
            decision.RecommendedOptionId, decision.SelectedOptionId, decision.FreeTextAnswer,
            decision.CreatedAt, decision.AnsweredAt) { AllowFreeText = ReadDecisionOptions(decision.OptionsJson).ConfigurationChange is null };
    }

    private static List<StoredOption> ReadOptions(string json) => ReadDecisionOptions(json).Options;

    // Legacy decisions stored a bare options array. New decisions also retain their typed action.
    private static StoredDecisionOptions ReadDecisionOptions(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? new(JsonSerializer.Deserialize<List<StoredOption>>(json, JsonOptions) ?? [], null)
            : JsonSerializer.Deserialize<StoredDecisionOptions>(json, JsonOptions)
                ?? throw new InvalidOperationException("The stored decision is invalid.");
    }

    private sealed record StoredDecisionOptions(List<StoredOption> Options, AgentConfigurationChoice? ConfigurationChange);

    private static string Required(string? value, int maximumLength, string name) =>
        Clean(value, maximumLength) ?? throw new ArgumentException($"{name} is required.");

    private static string? Clean(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        if (cleaned.Length > maximumLength) throw new ArgumentException($"Value exceeds {maximumLength} characters.");
        return cleaned;
    }

    private static AnswerExecutiveDecisionResponse Failure(string code, string message,
        ExecutiveDecisionCardResponse? decision = null) => new(false, code, message, decision);

    private sealed record StoredOption(string Id, string Label, string? Description);
}
