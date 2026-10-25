using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class ConnectorBindingService
{
    private async Task<IReadOnlyList<PluginAccountResourceChoice>> EnterpriseChoices(Guid organizationId, AgentInstallation requester,
        PluginDependencyDeclaration dependency, IReadOnlyList<string> requirements, CancellationToken ct)
    {
        var grants = await db.PluginAccountBusinessGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.RevokedAt == null).ToArrayAsync(ct);
        var result = new List<PluginAccountResourceChoice>();
        foreach (var grant in grants)
        {
            var connector = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant).SingleOrDefaultAsync(x =>
                x.Id == grant.PluginInstallationId && x.Scope == PluginInstallationScope.System && x.IsEnabled && x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active, ct);
            if (connector is null) continue;
            try { ValidatePackage(dependency, connector.PackageVersion!); } catch (InvalidOperationException) { continue; }
            var manifest = JsonSerializer.Deserialize<PluginManifest>(connector.PackageVersion!.ManifestJson, JsonOptions)!;
            if (manifest.Accounts is null || grant.PackageDigest != connector.PackageVersion.PackageDigest) continue;
            var selected = JsonSerializer.Deserialize<Guid[]>(grant.SelectedResourceIdsJson) ?? [];
            var resources = await db.PluginAccountResources.AsNoTracking().Where(x => x.AccountGroupId == grant.AccountGroupId && x.IsAvailable &&
                (grant.Scope == PluginAccountGrantScope.WholeAccount && grant.FutureResourcesAcknowledgedAt != null || selected.Contains(x.Id))).ToArrayAsync(ct);
            foreach (var resource in resources)
            {
                var authorizations = await db.PluginAccountResourceAuthorizations.AsNoTracking().Where(x => x.ResourceId == resource.Id && x.IsAvailable)
                    .Join(db.PluginConnections.Where(x => x.Status == PluginConnectionStatus.Connected), p => p.ConnectionId, c => c.Id, (p, c) => c).ToArrayAsync(ct);
                foreach (var connection in authorizations)
                {
                    var capability = requirements.FirstOrDefault(r => (JsonSerializer.Deserialize<string[]>(grant.CapabilitiesJson) ?? []).Contains(r));
                    if (capability is null) continue;
                    var candidate = new AgentCapabilityBinding { Id = Guid.NewGuid(), OrganizationId = organizationId.ToString("D"), ProviderInstallationId = connector.Id,
                        Capability = capability, PluginAccountBusinessGrantId = grant.Id, PluginAccountResourceId = resource.Id, PluginAccountConnectionId = connection.Id };
                    try { await new PluginAccountAuthorityService(db).RequireAsync(organizationId, candidate, connector, manifest, ct); } catch (UnauthorizedAccessException) { continue; }
                    var isSelected = await db.AgentCapabilityBindings.AsNoTracking().AnyAsync(x => x.RequesterInstallationId == requester.Id && x.DependencyId == dependency.Id &&
                        x.PluginAccountResourceId == resource.Id && x.PluginAccountConnectionId == connection.Id && x.RevokedAt == null, ct);
                    result.Add(new(connector.Id, resource.DisplayName, resource.Id, connection.Id, grant.Revision, grant.PackageDigest, isSelected));
                    break;
                }
            }
        }
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
