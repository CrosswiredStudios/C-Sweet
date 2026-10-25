using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public static class PluginAccountProfileGuard
{
    public static string Fingerprint(PluginOAuthProviderProfile p) => ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(
        new { p.Id, p.ClientId, p.AuthorizationEndpoint, p.TokenEndpoint, p.RevocationEndpoint,
            secretHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.ClientSecret))) }));

    public static async Task<PluginOAuthProviderProfile> RequireAsync(CSweetDbContext db, IPluginProviderProfileRegistry profiles,
        AgentInstallation installation, PluginConnectionDeclaration declaration, CancellationToken ct)
    {
        var profile = await profiles.ResolveAsync(declaration.ProviderProfile, ct)
            ?? throw new InvalidOperationException("provider_configuration_required");
        if (declaration.Provider is not { } provider || profile.AuthorizationEndpoint != provider.AuthorizationEndpoint ||
            profile.TokenEndpoint != provider.TokenEndpoint || profile.RevocationEndpoint != provider.RevocationEndpoint ||
            !await db.ConnectorProfileApprovals.AsNoTracking().AnyAsync(x => x.ConnectorInstallationId == installation.Id &&
                x.PackageDigest == installation.PackageVersion!.PackageDigest && x.ProfileId == profile.Id &&
                x.ProviderProfileHash == Fingerprint(profile) && x.RevokedAt == null, ct))
            throw new UnauthorizedAccessException("provider_review_required");
        return profile;
    }
}
