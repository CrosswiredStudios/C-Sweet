using CSweet.Contracts.Agents;

namespace CSweet.Application.Setup;

public interface IAgentInstallationService
{
    Task<AgentInstallationResponse> InstallAsync(
        Guid importId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse?> GetAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> UpdateScheduleAsync(
        Guid installationId,
        UpdateAgentScheduleRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> RunNowAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> RetryBuildAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> DisableAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> UpdateAsync(
        Guid installationId,
        UpdateAgentInstallationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> ApproveUpdateAsync(
        Guid stagedRevisionId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> EnableAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<RemoveAgentInstallationResponse> RemoveAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRuntimeRunResponse>> ListRunsAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentBuildLogResponse?> GetBuildLogAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);
}
