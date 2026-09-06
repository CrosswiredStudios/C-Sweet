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
            binding.ProviderInstallationId = connectorId;
            binding.DependencyId = dependencyId;
            binding.ProviderPackageDigest = connector.PackageVersion.PackageDigest;
            binding.GrantRevision = requester.Grant.GrantRevision;
            binding.Origin = AgentCapabilityBindingOrigins.Explicit;
            binding.ApprovedAt = DateTimeOffset.UtcNow;
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
