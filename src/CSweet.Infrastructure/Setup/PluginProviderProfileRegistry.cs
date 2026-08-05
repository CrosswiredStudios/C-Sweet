using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginProviderProfileRegistry(
    CSweetDbContext db,
    IDataProtectionProvider protection,
    IOptions<PluginConnectionOptions> deploymentOptions,
    IAuditEventWriter audit) : IPluginProviderProfileRegistry
{
    private readonly IDataProtector _protector = protection.CreateProtector("CSweet.PluginProviderProfiles.ClientSecret.v1");
    private readonly PluginConnectionOptions _deployment = deploymentOptions.Value;

    public async Task<PluginOAuthProviderProfile?> ResolveAsync(string id,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var stored = await db.PluginProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.IsEnabled, cancellationToken);
        if (stored is not null)
            return new(id, stored.DisplayName, stored.AuthorizationEndpoint, stored.TokenEndpoint,
                stored.RevocationEndpoint, stored.ClientId, _protector.Unprotect(stored.ProtectedClientSecret));
        if (!_deployment.Providers.TryGetValue(id, out var configured) ||
            string.IsNullOrWhiteSpace(configured.ClientId) || string.IsNullOrWhiteSpace(configured.ClientSecret))
            return null;
        ValidateEndpoint(configured.AuthorizationEndpoint, nameof(configured.AuthorizationEndpoint));
        ValidateEndpoint(configured.TokenEndpoint, nameof(configured.TokenEndpoint));
        if (!string.IsNullOrWhiteSpace(configured.RevocationEndpoint))
            ValidateEndpoint(configured.RevocationEndpoint, nameof(configured.RevocationEndpoint));
        return new(id, configured.DisplayName, configured.AuthorizationEndpoint, configured.TokenEndpoint,
            configured.RevocationEndpoint, configured.ClientId, configured.ClientSecret);
    }

    public async Task<IReadOnlyList<PluginProviderProfileResponse>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await db.PluginProviderProfiles.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var values = stored.Select(Map).ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var configured in _deployment.Providers.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (!values.ContainsKey(configured.Key))
                values[configured.Key] = new(configured.Key, configured.Value.DisplayName,
                    configured.Value.AuthorizationEndpoint, configured.Value.TokenEndpoint,
                    configured.Value.RevocationEndpoint, configured.Value.ClientId,
                    !string.IsNullOrWhiteSpace(configured.Value.ClientSecret), true, true, null);
        return values.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<PluginProviderProfileResponse> UpsertAsync(string id,
        UpsertPluginProviderProfileRequest request, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ValidateEndpoint(request.AuthorizationEndpoint, nameof(request.AuthorizationEndpoint));
        ValidateEndpoint(request.TokenEndpoint, nameof(request.TokenEndpoint));
        if (!string.IsNullOrWhiteSpace(request.RevocationEndpoint))
            ValidateEndpoint(request.RevocationEndpoint, nameof(request.RevocationEndpoint));
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200 ||
            string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 1024)
            throw new ArgumentException("A bounded display name and OAuth client ID are required.");
        var profile = await db.PluginProviderProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        var created = profile is null;
        if (profile is null)
        {
            if (string.IsNullOrWhiteSpace(request.ClientSecret))
                throw new ArgumentException("A client secret is required when creating a provider profile.");
            profile = new PluginProviderProfile { Id = id, CreatedAt = DateTimeOffset.UtcNow };
            db.PluginProviderProfiles.Add(profile);
        }
        profile.DisplayName = request.DisplayName.Trim();
        profile.AuthorizationEndpoint = request.AuthorizationEndpoint.Trim();
        profile.TokenEndpoint = request.TokenEndpoint.Trim();
        profile.RevocationEndpoint = string.IsNullOrWhiteSpace(request.RevocationEndpoint)
            ? null : request.RevocationEndpoint.Trim();
        profile.ClientId = request.ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            if (request.ClientSecret.Length > 4096) throw new ArgumentException("The client secret is too large.");
            profile.ProtectedClientSecret = _protector.Protect(request.ClientSecret);
        }
        profile.IsEnabled = request.IsEnabled;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(created ? "plugin-provider-profile.created" : "plugin-provider-profile.updated",
            nameof(PluginProviderProfile), Guid.Empty,
            $"{(created ? "Created" : "Updated")} OAuth provider profile {id}; secret value was not logged.",
            System.Text.Json.JsonSerializer.Serialize(new { profileId = id, profile.IsEnabled }), cancellationToken);
        return Map(profile);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (await db.PluginConnections.AnyAsync(x => x.ProviderProfile == id &&
                x.Status != PluginConnectionStatus.Revoked, cancellationToken))
            throw new InvalidOperationException("Disconnect every active installation before deleting this provider profile.");
        var profile = await db.PluginProviderProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return;
        db.PluginProviderProfiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-provider-profile.deleted", nameof(PluginProviderProfile), Guid.Empty,
            $"Deleted OAuth provider profile {id} and its encrypted client secret.", null, cancellationToken);
    }

    private static PluginProviderProfileResponse Map(PluginProviderProfile value) => new(
        value.Id, value.DisplayName, value.AuthorizationEndpoint, value.TokenEndpoint,
        value.RevocationEndpoint, value.ClientId, !string.IsNullOrWhiteSpace(value.ProtectedClientSecret),
        value.IsEnabled, false, value.UpdatedAt);

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 ||
            id.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or '-' or '_')))
            throw new ArgumentException("Provider profile IDs may contain only letters, digits, '.', '-' and '_'.");
    }

    private static void ValidateEndpoint(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.IsLoopback ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))
            throw new ArgumentException($"{name} must be a public HTTPS endpoint without credentials or fragments.");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xfe) != 0xfc && !address.Equals(IPAddress.IPv6Any);
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
               !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
               !(bytes[0] == 192 && bytes[1] == 168) &&
               !(bytes[0] == 198 && bytes[1] is 18 or 19);
    }
}
