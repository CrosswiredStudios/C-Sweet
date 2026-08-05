using CSweet.Contracts.Plugins;

namespace CSweet.Application.Setup;

public interface IPluginSetupService
{
    Task<PluginSetupResponse> GetAsync(Guid organizationId, Guid installationId, CancellationToken cancellationToken = default);
    Task<PluginSetupResponse> CompleteStepAsync(Guid organizationId, Guid installationId, string stepId,
        CompletePluginSetupStepRequest request, CancellationToken cancellationToken = default);
    Task<BeginPluginAuthorizationResponse> BeginAuthorizationAsync(Guid organizationId, Guid applicationUserId,
        Guid installationId, string connectionId, BeginPluginAuthorizationRequest request, string redirectUri,
        CancellationToken cancellationToken = default);
    Task<PluginAuthorizationCompletion> CompleteAuthorizationAsync(Guid applicationUserId, string code, string state,
        CancellationToken cancellationToken = default);
    Task<CompletePluginSetupResponse> ActivateAsync(Guid organizationId, Guid applicationUserId, Guid installationId,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync(Guid organizationId, Guid installationId, string connectionId,
        CancellationToken cancellationToken = default);
}
