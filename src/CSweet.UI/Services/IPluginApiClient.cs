using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

public interface IPluginApiClient
{
    Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken cancellationToken = default);
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
