using CSweet.Domain.Core;

namespace CSweet.Application.Communications;

public sealed record AgentCommunicationOnboardingResult(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    Guid? ConversationId = null,
    Guid? EventId = null);

public interface IAgentCommunicationOnboardingService
{
    Task<AgentCommunicationOnboardingResult> EnsureAsync(
        Guid organizationId,
        OrganizationUser agent,
        Guid? hiringApplicationUserId = null,
        bool queueLifecycleEvent = true,
        CancellationToken cancellationToken = default);
}
