using CSweet.Contracts.Plugins;

namespace CSweet.Application.Setup;

public sealed record PluginOAuthProviderProfile(
    string Id,
    string DisplayName,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RevocationEndpoint,
    string ClientId,
    string ClientSecret);

public interface IPluginProviderProfileRegistry
{
    Task<PluginOAuthProviderProfile?> ResolveAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PluginProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task<PluginProviderProfileResponse> UpsertAsync(string id, UpsertPluginProviderProfileRequest request,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
