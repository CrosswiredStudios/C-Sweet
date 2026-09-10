using CSweet.Contracts.Communications;

namespace CSweet.Application.Communications;

public sealed record CreateExecutiveDecisionOption(string Id, string Label, string? Description);

public sealed record CreateExecutiveDecisionCommand(
    Guid OrganizationId,
    Guid ConversationId,
    Guid? ChatTurnId,
    Guid? ConversationMessageId,
    Guid RequestingInstallationId,
    string Prompt,
    IReadOnlyList<CreateExecutiveDecisionOption> Options,
    string RecommendedOptionId,
    string IdempotencyKey)
{
    public AgentConfigurationChoice? ConfigurationChange { get; init; }
    public Guid? WorkstreamDecisionId { get; init; }
}

/// <summary>A user-confirmed change to a preset field on the requesting employee.</summary>
public sealed record AgentConfigurationChoice(string Key, string CurrentValue, string ProposedValue);

public interface IExecutiveDecisionService
{
    Task<IReadOnlyList<PendingAgentQuestionResponse>> ListPendingForUserAsync(Guid organizationId,
        Guid actorOrganizationUserId, CancellationToken cancellationToken = default);
    Task<ExecutiveDecisionCardResponse> CreateAsync(CreateExecutiveDecisionCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, ExecutiveDecisionCardResponse>> ListForMessagesAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<AnswerExecutiveDecisionResponse> AnswerAsync(Guid organizationId, Guid conversationId, Guid decisionId,
        Guid actorOrganizationUserId, AnswerExecutiveDecisionRequest request, CancellationToken cancellationToken = default);
    Task CancelPendingForTurnAsync(Guid chatTurnId, CancellationToken cancellationToken = default);
}
