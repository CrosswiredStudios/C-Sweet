using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Explicit, same-organization connection selection. Never auto-selects a channel.</summary>
public sealed class ConnectorBindingService(CSweetDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyList<PluginDependencySetupResponse>> GetChoicesAsync(
        Guid organizationId, Guid requesterId, CancellationToken token)
    {
        var organization = organizationId.ToString("D");
        var requester = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == requesterId && x.BusinessId == organization && x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active, token)
            ?? throw new InvalidOperationException("The requesting installation is unavailable.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(requester.PackageVersion!.ManifestJson, JsonOptions)!;
        if (manifest.Dependencies.Count == 0) return [];
        var connectors = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .Include(x => x.Connections).Where(x => x.BusinessId == organization && (x.IsEnabled || x.SetupState == PluginSetupState.ConnectionRequired) &&
                x.RevisionStatus == PluginRevisionStatus.Active && x.Scope == PluginInstallationScope.Organization &&
                x.PackageVersion!.PluginKind == PluginKind.Connector).ToListAsync(token);
        var approvals = await db.ConnectorProfileApprovals.AsNoTracking()
            .Where(x => connectors.Select(c => c.Id).Contains(x.ConnectorInstallationId) && x.RevokedAt == null)
            .ToListAsync(token);
        var bindings = await db.AgentCapabilityBindings.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.RequesterInstallationId == requesterId && x.RevokedAt == null).ToListAsync(token);
        var granted = JsonSerializer.Deserialize<string[]>(requester.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        var result = new List<PluginDependencySetupResponse>();
        foreach (var dependency in manifest.Dependencies)
        {
            var required = manifest.Requires.Where(x => x.Dependency == dependency.Id).Select(x => x.Name).ToArray();
            var choices = new List<PluginConnectorChoice>();
            foreach (var connector in connectors)
            {
                try { ValidatePackage(dependency, connector.PackageVersion!); }
                catch (InvalidOperationException) { continue; }
                var provider = JsonSerializer.Deserialize<PluginManifest>(connector.PackageVersion!.ManifestJson, JsonOptions)!;
                var provided = JsonSerializer.Deserialize<string[]>(connector.Grant?.ProvidedCapabilitiesJson ?? "[]") ?? [];
                var accounts = connector.Connections.Where(x => x.Status == PluginConnectionStatus.Connected &&
                    !string.IsNullOrWhiteSpace(x.BoundResourceId)).Select(x => new PluginConnectedAccount(
                        x.BoundResourceId!, x.ExternalAccountName ?? x.BoundResourceId!)).ToArray();
                var reason = required.Length == 0 || required.Except(granted).Any() ||
                    required.Except(provided).Any() || required.Any(x => !provider.Provides.Any(p => p.Name == x))
                    ? "An administrator must approve the required access."
                    : provider.Connections.Any(c => !approvals.Any(a => a.ConnectorInstallationId == connector.Id &&
                        a.PackageDigest == connector.PackageVersion.PackageDigest && a.ProfileId == c.ProviderProfile))
                        ? "An administrator must approve this connection package."
                        : !connector.IsEnabled || connector.SetupState != PluginSetupState.Ready || accounts.Length == 0
                            ? "Finish connecting and confirming the account." : null;
                choices.Add(new(connector.Id, provider.Name, connector.SetupState.ToString(), reason is null, reason, accounts));
            }
            var current = bindings.Where(x => x.DependencyId == dependency.Id && x.GrantRevision == requester.Grant?.GrantRevision).ToArray();
            Guid? selected = current.Select(x => x.ProviderInstallationId).Distinct().Count() == 1 &&
                required.Length > 0 && required.All(x => current.Any(b => b.Capability == x)) &&
                current.All(b => connectors.Any(c => c.Id == b.ProviderInstallationId && c.PackageVersion!.PackageDigest == b.ProviderPackageDigest))
                ? current[0].ProviderInstallationId : null;
            result.Add(new(dependency.Id, dependency.PluginId, required, selected,
                choices.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.InstallationId).ToArray()));
        }
        return result;
    }

    public async Task ValidateRequiredBindingsAsync(AgentInstallation requester, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(requester.PackageVersion!.ManifestJson, JsonOptions)!;
        foreach (var dependency in manifest.Dependencies)
        {
            var requirements = manifest.Requires.Where(x => x.Dependency == dependency.Id).ToArray();
            if (requirements.Length == 0) throw new InvalidOperationException("A required dependency has no capability requirements.");
            var bindings = await db.AgentCapabilityBindings.Include(x => x.ProviderInstallation!).ThenInclude(x => x.PackageVersion)
                .Where(x => x.RequesterInstallationId == requester.Id && x.OrganizationId == requester.BusinessId &&
                    x.DependencyId == dependency.Id && x.GrantRevision == requester.Grant!.GrantRevision && x.RevokedAt == null)
                .ToListAsync(token);
            if (bindings.Select(x => x.ProviderInstallationId).Distinct().Count() != 1 ||
                requirements.Any(x => !bindings.Any(b => b.Capability == x.Name)))
                throw new InvalidOperationException("Select and authorize an account for every required connector capability.");
            var connector = bindings[0].ProviderInstallation!;
            ValidatePackage(dependency, connector.PackageVersion!);
            if (connector.BusinessId != requester.BusinessId || !connector.IsEnabled || connector.SetupState != PluginSetupState.Ready ||
                connector.RevisionStatus != PluginRevisionStatus.Active ||
                bindings.Any(x => x.ProviderPackageDigest != connector.PackageVersion!.PackageDigest) ||
                !await db.PluginConnections.AnyAsync(x => x.AgentInstallationId == connector.Id &&
                    x.Status == PluginConnectionStatus.Connected && x.BoundResourceId != null, token))
                throw new InvalidOperationException("A required connector is disconnected, unconfirmed or has changed since approval.");
        }
    }

    public async Task BindAsync(Guid organizationId, Guid requesterId, string dependencyId, Guid connectorId, CancellationToken token)
    {
        var choices = await GetChoicesAsync(organizationId, requesterId, token);
        if (!choices.Any(x => x.Id == dependencyId && x.Choices.Any(c => c.InstallationId == connectorId && c.CanSelect)))
            throw new InvalidOperationException("Choose an approved, connected account before authorizing access.");
        var requester = await db.AgentInstallations.Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == requesterId && x.BusinessId == organizationId.ToString("D") &&
                x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token)
            ?? throw new InvalidOperationException("The requesting installation is unavailable.");
        var connector = await db.AgentInstallations.Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == connectorId && x.BusinessId == requester.BusinessId && x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active && x.Scope == PluginInstallationScope.Organization, token)
            ?? throw new InvalidOperationException("Select a connector in this organization.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(requester.PackageVersion!.ManifestJson, JsonOptions)!;
        var dependency = manifest.Dependencies.SingleOrDefault(x => x.Id == dependencyId)
            ?? throw new InvalidOperationException("The dependency is not declared.");
        ValidatePackage(dependency, connector.PackageVersion!);
        var provider = JsonSerializer.Deserialize<PluginManifest>(connector.PackageVersion!.ManifestJson, JsonOptions)!;
        var requesterGrants = JsonSerializer.Deserialize<string[]>(requester.Grant!.RequiredCapabilitiesJson, JsonOptions) ?? [];
        var providerGrants = JsonSerializer.Deserialize<string[]>(connector.Grant!.ProvidedCapabilitiesJson, JsonOptions) ?? [];
        var required = manifest.Requires.Where(x => x.Dependency == dependencyId && requesterGrants.Contains(x.Name)).ToArray();
        if (required.Length == 0) throw new InvalidOperationException("No consuming capabilities have been approved.");
        if (required.Any(x => !providerGrants.Contains(x.Name) || !provider.Provides.Any(p => p.Name == x.Name)))
            throw new InvalidOperationException("The selected connector does not provide every granted dependency capability.");
        var old = await db.AgentCapabilityBindings.Where(x => x.RequesterInstallationId == requesterId &&
            x.DependencyId == dependencyId && x.RevokedAt == null).ToListAsync(token);
        if (old.Any(x => x.ProviderInstallationId != connectorId ||
            x.ProviderPackageDigest != connector.PackageVersion.PackageDigest || x.GrantRevision != requester.Grant.GrantRevision))
        {
            // Changing accounts or reviewed authority never carries autonomous permission forward.
            await ConnectorStandingPolicyService.RevokeForConsumersAsync(db, organizationId, [requesterId], token);
            var policies = await db.PluginStandingPolicies.Where(x => x.OrganizationId == organizationId &&
                x.AgentInstallationId == requesterId && x.Status == PluginStandingPolicyStatus.Approved).ToListAsync(token);
            foreach (var policy in policies)
            {
                policy.Status = PluginStandingPolicyStatus.Revoked;
                policy.RevokedAt = DateTimeOffset.UtcNow;
            }
        }
        // Existing rows are updated in place under the scoped unique index; grants remain explicit.
        foreach (var requirement in required)
        {
            var binding = old.SingleOrDefault(x => x.Capability == requirement.Name);
            if (binding is null)
            {
                binding = new AgentCapabilityBinding { Id = Guid.NewGuid(), RequesterInstallationId = requesterId,
                    OrganizationId = requester.BusinessId, Capability = requirement.Name };
                db.AgentCapabilityBindings.Add(binding);
            }
            var authorityChanged = binding.ProviderInstallationId != connectorId ||
                binding.ProviderPackageDigest != connector.PackageVersion.PackageDigest ||
                binding.GrantRevision != requester.Grant.GrantRevision || binding.RevokedAt is not null;
            binding.ProviderInstallationId = connectorId;
            binding.DependencyId = dependencyId;
            binding.ProviderPackageDigest = connector.PackageVersion.PackageDigest;
            binding.GrantRevision = requester.Grant.GrantRevision;
            binding.Origin = AgentCapabilityBindingOrigins.Explicit;
            if (authorityChanged) binding.ApprovedAt = DateTimeOffset.UtcNow;
        }
        foreach (var removed in old.Where(x => !required.Any(r => r.Name == x.Capability))) removed.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }

    public static void ValidatePackage(PluginDependencyDeclaration dependency, AgentPackageVersion package)
    {
        if (package.PluginKind != PluginKind.Connector || package.AgentId != dependency.PluginId ||
            package.PublisherId != dependency.PublisherId || string.IsNullOrWhiteSpace(package.PackageDigest) ||
            !Version.TryParse(package.Version, out var version) || version < Version.Parse(dependency.MinimumVersion) ||
            version >= Version.Parse(dependency.MaximumVersionExclusive))
            throw new InvalidOperationException("The connector identity, version or immutable build does not satisfy this dependency.");
    }
}
