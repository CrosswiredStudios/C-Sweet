using CSweet.Agent.SDK;

namespace CSweet.Application.Communications;

public interface IAgentCoordinationService
{
    Task<AgentCoordinationSession> StartAsync(
        Guid organizationId,
        Guid initiatorOrganizationUserId,
        Guid initiatorInstallationId,
        StartAgentCoordinationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentCoordinationSession> StartWorkAsync(
        Guid organizationId,
        Guid initiatorOrganizationUserId,
        Guid initiatorInstallationId,
        StartWorkItemCoordinationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentCoordinationSession> RespondAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid actorInstallationId,
        RespondToAgentCoordinationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentCoordinationSession?> ReadAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentCoordinationSession>> ListAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid? chatId,
        bool activeOnly,
        CancellationToken cancellationToken = default);

    Task<AgentCoordinationSession> ResumeAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid actorInstallationId,
        ResumeAgentCoordinationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentCoordinationSession> CancelAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        bool actorCanManage,
        CancelAgentCoordinationRequest request,
        CancellationToken cancellationToken = default);
}
