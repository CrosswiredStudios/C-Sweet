using CSweet.Contracts.Agents;

namespace CSweet.UI.Services;

public interface IAgentApiClient
{
    Task<AgentImportPreviewResponse> PreviewImportAsync(
        PreviewAgentImportRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> InstallAsync(
        Guid importId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInstallationResponse>> ListInstallationsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInstallationResponse>> ListDefinitionsAsync(
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse?> GetDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> RetryDefinitionBuildAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default);

    Task<AgentConfigurationView> GetDefinitionConfigurationAsync(
        Guid definitionId, CancellationToken cancellationToken = default);

    Task<AgentConfigurationView> UpdateDefinitionConfigurationAsync(
        Guid definitionId, PutAgentDefinitionConfigurationRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentConfigurationView> GetEmployeeConfigurationAsync(
        Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);

    Task<AgentConfigurationView> UpdateEmployeeConfigurationAsync(
        Guid organizationId, Guid employeeId, PutAgentConfigurationOverridesRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentConfigurationView> RestoreEmployeeConfigurationKeyAsync(
        Guid organizationId, Guid employeeId, string key, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<AgentConfigurationView> RestoreAllEmployeeConfigurationAsync(
        Guid organizationId, Guid employeeId, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse?> GetInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentUpdateAvailabilityResponse>> CheckUpdatesAsync(
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> UpdateAsync(
        Guid installationId,
        UpdateAgentInstallationRequest request,
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

    Task<AgentInstallationResponse> RetryStartupAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentInstallationResponse> DisableAsync(
        Guid installationId,
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

    Task<AgentBuildLogResponse> GetBuildLogAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeReadinessResponse> EnsureRuntimeAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeReadinessResponse> GetRuntimeStatusAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    Task<AgentConfigurationSchemaResponse> GetConfigurationAsync(
        string installationId,
        CancellationToken cancellationToken = default);

    Task<AgentConfigurationUpdateResponse> UpdateConfigurationAsync(
        string installationId,
        UpdateAgentConfigurationRequest request,
        CancellationToken cancellationToken = default);
}
