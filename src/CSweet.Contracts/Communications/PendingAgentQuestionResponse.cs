namespace CSweet.Contracts.Communications;

public sealed record PendingAgentQuestionResponse(
    Guid ConversationId, Guid AgentOrganizationUserId, string AgentName,
    ExecutiveDecisionCardResponse Decision);
