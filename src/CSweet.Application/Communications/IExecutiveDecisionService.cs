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
    string IdempotencyKey);

public interface IExecutiveDecisionService
{
    Task<ExecutiveDecisionCardResponse> CreateAsync(CreateExecutiveDecisionCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, ExecutiveDecisionCardResponse>> ListForMessagesAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<AnswerExecutiveDecisionResponse> AnswerAsync(Guid organizationId, Guid conversationId, Guid decisionId,
        Guid actorOrganizationUserId, AnswerExecutiveDecisionRequest request, CancellationToken cancellationToken = default);
    Task CancelPendingForTurnAsync(Guid chatTurnId, CancellationToken cancellationToken = default);
}
