using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

public interface IPluginApiClient
{
    Task<CSweet.Contracts.Core.ToolchainCertificationSetup> GetToolchainSetupAsync(Guid organizationId, CancellationToken ct = default) => throw new NotSupportedException();
    Task<IReadOnlyList<CSweet.Contracts.Core.ToolchainCertificationSummary>> GetToolchainCertificationsAsync(Guid organizationId, CancellationToken ct = default) => throw new NotSupportedException();
    Task<CSweet.Contracts.Core.ToolchainCertificationSummary> StartToolchainCertificationAsync(CSweet.Contracts.Core.StartToolchainCertificationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    Task<CSweet.Contracts.Core.ToolchainCertificationSummary> ResumeToolchainCertificationAsync(Guid id,
        CSweet.Contracts.Core.ResumeToolchainCertificationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    Task<AgentInstallationResponse> StageUpdateAsync(Guid installationId,Guid packageId,CancellationToken ct=default)=>throw new NotSupportedException();
    Task<AgentInstallationResponse> ApproveUpdateAsync(Guid stagedId,InstallAgentRequest request,CancellationToken ct=default)=>throw new NotSupportedException();
    Task<AgentBuildLogResponse?> GetBuildLogAsync(Guid installationId,CancellationToken ct=default)=>Task.FromResult<AgentBuildLogResponse?>(null);
    Task<PluginInstallationPolicyResponse> GetInstallationPolicyAsync(CancellationToken cancellationToken = default);
    Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken cancellationToken = default);
    Task<AgentImportPreviewResponse> PreviewBundleAsync(Stream bundle, string fileName, CancellationToken cancellationToken = default);
    Task<AgentInstallationResponse> InstallAsync(Guid importId, InstallAgentRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(Guid installationId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
    Task SetSecretAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default);
    Task<AgentInstallationResponse> SetEnabledAsync(Guid installationId, bool enabled, CancellationToken cancellationToken = default);
    Task<RemoveAgentInstallationResponse> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PluginProviderProfileResponse>> ListProviderProfilesAsync(CancellationToken cancellationToken = default);
    Task<PluginProviderProfileResponse> SaveProviderProfileAsync(string id,
        UpsertPluginProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task DeleteProviderProfileAsync(string id, CancellationToken cancellationToken = default);
}
