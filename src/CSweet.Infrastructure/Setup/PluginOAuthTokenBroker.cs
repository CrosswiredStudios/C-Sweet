using System.Collections.Concurrent;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginOAuthTokenBroker(
    IPluginSecretStore secrets,
    CSweetDbContext db,
    IHttpClientFactory httpClientFactory,
    IPluginProviderProfileRegistry providerProfiles) : IPluginOAuthTokenBroker
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string?> GetAccessTokenAsync(Guid installationId, PluginConnection connection,
        CancellationToken cancellationToken = default)
    {
        var key = $"oauth.connection.{connection.Id:N}.token";
        var token = await ReadAsync(installationId, key, cancellationToken);
        if (token is null) return null;
        if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return token.AccessToken;
        if (string.IsNullOrWhiteSpace(token.RefreshToken)) return null;
        var profile = await providerProfiles.ResolveAsync(connection.ProviderProfile, cancellationToken);
        if (profile is null || !Uri.TryCreate(profile.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint) ||
            tokenEndpoint.Scheme != Uri.UriSchemeHttps) return null;

        var gate = RefreshLocks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            token = await ReadAsync(installationId, key, cancellationToken);
            if (token is null) return null;
            if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return token.AccessToken;
            using var refresh = await httpClientFactory.CreateClient(nameof(PluginOAuthTokenBroker)).PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = profile.ClientId,
                    ["client_secret"] = profile.ClientSecret,
                    ["refresh_token"] = token.RefreshToken!,
                    ["grant_type"] = "refresh_token"
                }), cancellationToken);
            if (!refresh.IsSuccessStatusCode)
            {
                if (refresh.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
                    await MarkReauthorizationRequiredAsync(installationId, connection.Id, key, cancellationToken);
                return null;
            }
            using var body = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync(cancellationToken));
            var accessToken = body.RootElement.TryGetProperty("access_token", out var accessNode)
                ? accessNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            token = token with
            {
                AccessToken = accessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    body.RootElement.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600)
            };
            await secrets.SetAsync(installationId, key, JsonSerializer.Serialize(token, JsonOptions), cancellationToken);
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TokenEnvelope?> ReadAsync(Guid installationId, string key, CancellationToken cancellationToken)
    {
        var value = await secrets.GetAsync(installationId, key, cancellationToken);
        return value is null ? null : JsonSerializer.Deserialize<TokenEnvelope>(value, JsonOptions);
    }

    private async Task MarkReauthorizationRequiredAsync(Guid installationId, Guid connectionId, string tokenKey,
        CancellationToken cancellationToken)
    {
        var installation = await db.AgentInstallations.Include(x => x.Schedule).Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        var tracked = await db.PluginConnections.SingleOrDefaultAsync(x => x.Id == connectionId &&
            x.AgentInstallationId == installationId, cancellationToken);
        if (installation is null || tracked is null) return;
        tracked.Status = PluginConnectionStatus.ReauthorizationRequired;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
        installation.SetupState = PluginSetupState.ConnectionRequired;
        installation.IsEnabled = false;
        if (installation.Schedule is not null) installation.Schedule.IsEnabled = false;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion?.ManifestJson ?? "{}", JsonOptions);
        var flow = manifest?.Setup?.Flows.SingleOrDefault(x => x.Id == manifest.Setup.EntryFlow);
        installation.SetupFlowId = flow?.Id;
        installation.SetupStepId = flow?.Steps.FirstOrDefault(x => x.Kind == "oauth-connect" &&
            x.Connection == tracked.DeclarationId)?.Id;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        await secrets.RemoveAsync(installationId, tokenKey, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record TokenEnvelope(string AccessToken, string? RefreshToken, string TokenType,
        DateTimeOffset ExpiresAt);
}
