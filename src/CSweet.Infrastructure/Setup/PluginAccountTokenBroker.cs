using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Database-fenced refresh for enterprise authorizations; failures do not disable sibling accounts.</summary>
public sealed class PluginAccountTokenBroker(CSweetDbContext db, IPluginSecretStore secrets, IHttpClientFactory http, IPluginProviderProfileRegistry profiles)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<string?> GetAsync(Guid installationId, Guid connectionId, CancellationToken ct)
    {
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId &&
            x.AgentInstallationId == installationId && x.AccountGroupId != null && x.Status == PluginConnectionStatus.Connected, ct);
        if (connection?.ActiveCredentialAttemptId is not { } generation || !await Available(connection, ct)) return null;
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleAsync(x => x.Id == installationId, ct);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, Json)!;
        var profile = await PluginAccountProfileGuard.RequireAsync(db, profiles, installation, manifest.Connections.Single(x => x.Id == connection.DeclarationId), ct);
        if (PluginAccountProfileGuard.Fingerprint(profile) != connection.CredentialProfileHash) return null;
        var key = PluginOAuthCredentialKeys.Generation(connectionId, generation);
        var json = await secrets.GetAsync(installationId, key, ct);
        if (json is null) return null;
        var envelope = JsonSerializer.Deserialize<PluginAccountTokenEnvelope>(json, Json)!;
        if (envelope.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return envelope.AccessToken;
        var owner = Guid.NewGuid();
        var tracked = await db.PluginConnections.SingleAsync(x => x.Id == connectionId, ct);
        await db.Entry(tracked).ReloadAsync(ct);
        if (tracked.RefreshLeaseUntil > DateTimeOffset.UtcNow || tracked.ActiveCredentialAttemptId != generation || tracked.Status != PluginConnectionStatus.Connected) return null;
        tracked.RefreshLeaseOwner = owner; tracked.RefreshLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return null; }
        string? nextKey = null;
        try
        {
            if (string.IsNullOrWhiteSpace(envelope.RefreshToken)) { await RequireReconnect(connectionId, owner, ct); return null; }
            using var response = await http.CreateClient(nameof(PluginOAuthTokenBroker)).PostAsync(profile.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = profile.ClientId, ["client_secret"] = profile.ClientSecret,
                    ["grant_type"] = "refresh_token", ["refresh_token"] = envelope.RefreshToken }), ct);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 400 or 401) await RequireReconnect(connectionId, owner, ct);
                return null;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement; _ = ConnectorRequestMaterializer.Canonical(root);
            var access = root.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(access) || access.Length > 8192 || root.TryGetProperty("token_type", out var type) &&
                !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) return null;
            var expires = root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600;
            if (expires is < 1 or > 86400) return null;
            // A refresh cannot silently widen or narrow reviewed authority. Scope loss requires a new consent/import review.
            if (root.TryGetProperty("scope", out var refreshedScopes) && (refreshedScopes.ValueKind != JsonValueKind.String ||
                JsonSerializer.Deserialize<string[]>(connection.GrantedScopesJson, Json)!.Except(
                    refreshedScopes.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal).Any()))
            { await RequireReconnect(connectionId, owner, ct); return null; }
            var currentProfile = await PluginAccountProfileGuard.RequireAsync(db, profiles, installation, manifest.Connections.Single(x => x.Id == connection.DeclarationId), ct);
            if (PluginAccountProfileGuard.Fingerprint(currentProfile) != connection.CredentialProfileHash) return null;
            if (!await Available(connection, ct) || !await Owns(connectionId, generation, owner, ct)) return null;
            var next = Guid.NewGuid(); nextKey = PluginOAuthCredentialKeys.Generation(connectionId, next);
            var updated = envelope with { AccessToken = access, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires),
                RefreshToken = root.TryGetProperty("refresh_token", out var rotated) && rotated.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rotated.GetString()) ? rotated.GetString() : envelope.RefreshToken };
            if (updated.RefreshToken?.Length > 8192) return null;
            await secrets.SetAsync(installationId, nextKey, JsonSerializer.Serialize(updated, Json), ct);
            if (!await Available(connection, ct) || !await Owns(connectionId, generation, owner, ct)) return null;
            await db.Entry(tracked).ReloadAsync(ct);
            tracked.ActiveCredentialAttemptId = next; tracked.RefreshLeaseOwner = null; tracked.RefreshLeaseUntil = null;
            await db.SaveChangesAsync(ct);
            nextKey = null; await secrets.RemoveAsync(installationId, key, ct);
            return access;
        }
        finally
        {
            if (nextKey is not null) await secrets.RemoveAsync(installationId, nextKey, CancellationToken.None);
            await db.Entry(tracked).ReloadAsync(CancellationToken.None);
            if (tracked.RefreshLeaseOwner == owner)
            {
                tracked.RefreshLeaseOwner = null; tracked.RefreshLeaseUntil = null;
                try { await db.SaveChangesAsync(CancellationToken.None); } catch (DbUpdateConcurrencyException) { }
            }
        }
    }
    private Task<bool> Owns(Guid id, Guid generation, Guid owner, CancellationToken ct) => db.PluginConnections.AsNoTracking().AnyAsync(x => x.Id == id &&
        x.ActiveCredentialAttemptId == generation && x.RefreshLeaseOwner == owner && x.RefreshLeaseUntil > DateTimeOffset.UtcNow && x.Status == PluginConnectionStatus.Connected, ct);
    private Task<bool> Available(PluginConnection c, CancellationToken ct) => db.PluginConnections.AsNoTracking().AnyAsync(x => x.Id == c.Id &&
        x.AuthorizationRevision == c.AuthorizationRevision && x.Status == PluginConnectionStatus.Connected && x.AgentInstallation!.IsEnabled &&
        x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active && db.PluginAccountGroups.Any(g => g.Id == x.AccountGroupId &&
            g.State != PluginAccountState.Disconnecting && g.State != PluginAccountState.Disconnected), ct);
    private async Task RequireReconnect(Guid id, Guid owner, CancellationToken ct)
    {
        var connection = await db.PluginConnections.SingleAsync(x => x.Id == id, ct); await db.Entry(connection).ReloadAsync(ct);
        if (connection.RefreshLeaseOwner != owner || connection.Status != PluginConnectionStatus.Connected) return;
        connection.Status = PluginConnectionStatus.ReauthorizationRequired; connection.AuthorizationRevision++; connection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
