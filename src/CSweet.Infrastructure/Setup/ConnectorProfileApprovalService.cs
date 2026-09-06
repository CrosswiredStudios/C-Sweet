using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Called exclusively by administrator-authorized HTTP controls, never by plugin capabilities.</summary>
public sealed class ConnectorProfileApprovalService(CSweetDbContext db, IPluginProviderProfileRegistry profiles, IAuditEventWriter audit)
{
    public async Task ApproveAsync(Guid organizationId, Guid connectorId, Guid administratorId,
        string expectedPackageDigest, string profileId, CancellationToken token)
    {
        var connector = await db.AgentInstallations.Include(x => x.PackageVersion).SingleOrDefaultAsync(x =>
            x.Id == connectorId && x.BusinessId == organizationId.ToString("D") &&
            x.PackageVersion!.PluginKind == PluginKind.Connector && x.RevisionStatus == PluginRevisionStatus.Active, token)
            ?? throw new UnauthorizedAccessException("The connector is not in this organization.");
        if (string.IsNullOrWhiteSpace(expectedPackageDigest) || connector.PackageVersion!.PackageDigest != expectedPackageDigest)
            throw new InvalidOperationException("Review the actual immutable connector build before approving a profile.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(connector.PackageVersion.ManifestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        PluginManifestReader.ValidateConnectorContracts(manifest);
        var declaration = manifest.Connections.Single();
        var provider = declaration.Provider!;
        var profile = await profiles.ResolveAsync(profileId, token)
            ?? throw new InvalidOperationException("Configure this OAuth profile in the administrator vault first.");
        if (declaration.ProviderProfile != profileId || profile.AuthorizationEndpoint != provider.AuthorizationEndpoint ||
            profile.TokenEndpoint != provider.TokenEndpoint || profile.RevocationEndpoint != provider.RevocationEndpoint)
            throw new InvalidOperationException("The OAuth profile does not exactly match the reviewed provider endpoints.");
        var approval = await db.ConnectorProfileApprovals.SingleOrDefaultAsync(x => x.ConnectorInstallationId == connectorId &&
            x.PackageDigest == expectedPackageDigest && x.ProfileId == profileId, token);
        if (approval is null)
        {
            approval = new() { Id = Guid.NewGuid(), ConnectorInstallationId = connectorId,
                PackageDigest = expectedPackageDigest, ProfileId = profileId };
            db.ConnectorProfileApprovals.Add(approval);
        }
        approval.ApprovedAt = DateTimeOffset.UtcNow; approval.RevokedAt = null;
        approval.ApprovedByApplicationUserId = administratorId;
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("connector.profile.approved", nameof(ConnectorProfileApproval), approval.Id,
            $"Administrator {administratorId:D} approved build {expectedPackageDigest} for profile {profileId}.", null, token);
    }
}
