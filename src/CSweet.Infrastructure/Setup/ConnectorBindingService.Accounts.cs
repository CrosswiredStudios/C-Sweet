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
        var grantRevision = requester.Grant?.GrantRevision;
        var bindings = await db.AgentCapabilityBindings.AsNoTracking().Where(x => x.RequesterInstallationId == requester.Id &&
            x.OrganizationId == requester.BusinessId && x.DependencyId == dependency.Id &&
            x.GrantRevision == grantRevision && x.RevokedAt == null).ToArrayAsync(ct);
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
                    var isSelected = bindings.Length > 0 && bindings.All(x => x.ProviderInstallationId == connector.Id &&
                        x.PluginAccountBusinessGrantId == grant.Id && x.PluginAccountResourceId == resource.Id && x.PluginAccountConnectionId == connection.Id);
                    if (isSelected)
                    {
                        // Selected drives the setup readiness indicator. Reuse activation's
                        // current binding-set guard rather than trusting one surviving row.
                        try { await ValidateNativeBindings(requester, connector, manifest, requirements, bindings, ct); }
                        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException) { isSelected = false; }
                    }
                    result.Add(new(connector.Id, resource.DisplayName, resource.Id, connection.Id, grant.Revision, grant.PackageDigest, isSelected));
                    break;
                }
            }
        }
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    private async Task ValidateNativeBindings(AgentInstallation requester, AgentInstallation connector, PluginManifest provider,
        IReadOnlyList<string> requirements, IReadOnlyList<AgentCapabilityBinding> bindings, CancellationToken ct)
    {
        var baseRequired = requirements.Where(r => provider.ProviderOperations.Any(o => o.Capability == r && o.Http is { Bootstrap: false } mapping &&
            mapping.ScopeSets.All(id => provider.Connections.Single(c => c.Id == mapping.Connection).ScopeSets.Single(s => s.Id == id).Required))).ToArray();
        if (requirements.Count == 0 || bindings.Count == 0 || !connector.IsEnabled || connector.SetupState != PluginSetupState.Ready ||
            connector.RevisionStatus != PluginRevisionStatus.Active || bindings.Any(b => b.ProviderInstallationId != connector.Id ||
                b.RequesterInstallationId != requester.Id || b.OrganizationId != requester.BusinessId || b.RevokedAt != null ||
                b.GrantRevision != requester.Grant?.GrantRevision || b.ProviderPackageDigest != connector.PackageVersion!.PackageDigest ||
                !requirements.Contains(b.Capability, StringComparer.Ordinal)) ||
            baseRequired.Any(r => !bindings.Any(b => b.Capability == r)) || bindings.Select(b => b.PluginAccountResourceId).Distinct().Count() != 1)
            throw new InvalidOperationException("Select a currently authorized resource with the required base permissions.");
        foreach (var binding in bindings)
            await new PluginAccountAuthorityService(db).RequireAsync(Guid.Parse(requester.BusinessId), binding, connector, provider, ct);
    }
}
