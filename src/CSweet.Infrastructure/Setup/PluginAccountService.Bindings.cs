using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    public async Task BindBusinessResourceAsync(Guid userId, Guid organizationId, Guid installationId, PluginAccountBindingRequest request, CancellationToken ct)
    {
        if (!await Access.IsActiveHumanAsync(userId, ct) || !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.ApplicationUserId == userId && x.OrganizationId == organizationId && x.IsActive &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel >= OrganizationPermissionLevel.Manager, ct)) throw new UnauthorizedAccessException("Business management authority is required.");
        var (connector, manifest, declaration) = await Context(installationId, ct);
        var resource = await db.PluginAccountResources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ResourceId && x.PluginInstallationId == installationId && x.IsAvailable, ct)
            ?? throw new UnauthorizedAccessException("The resource is unavailable.");
        var grant = await db.PluginAccountBusinessGrants.AsNoTracking().SingleOrDefaultAsync(x => x.PluginInstallationId == installationId && x.AccountGroupId == resource.AccountGroupId &&
            x.OrganizationId == organizationId && x.RevokedAt == null && x.Revision == request.BusinessGrantRevision, ct) ?? throw new UnauthorizedAccessException("Review the current business grant.");
        var requester = await db.AgentInstallations.Include(x => x.PackageVersion).Include(x => x.Grant).SingleOrDefaultAsync(x => x.Id == request.RequesterInstallationId &&
            x.BusinessId == organizationId.ToString("D") && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct) ?? throw new UnauthorizedAccessException("The consuming agent is unavailable in this business.");
        var consumer = JsonSerializer.Deserialize<PluginManifest>(requester.PackageVersion!.ManifestJson, Json)!;
        var dependency = consumer.Dependencies.SingleOrDefault(x => x.Id == request.DependencyId) ?? throw new UnauthorizedAccessException("The agent did not declare this dependency.");
        ConnectorBindingService.ValidatePackage(dependency, connector.PackageVersion!);
        if (request.PackageDigest != connector.PackageVersion!.PackageDigest || grant.PackageDigest != request.PackageDigest) throw new UnauthorizedAccessException("The reviewed connector changed.");
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ConnectionId && x.AccountGroupId == resource.AccountGroupId &&
            x.AgentInstallationId == installationId && x.Status == PluginConnectionStatus.Connected, ct) ?? throw new UnauthorizedAccessException("Connect this resource first.");
        var bindingHash = managed?.UsesManaged(declaration.ProviderProfile) == true
            ? (await managed.Availability(connector.PackageVersion!.PackageDigest!, ct)).BindingHash
            : PluginAccountProfileGuard.Fingerprint(await PluginAccountProfileGuard.RequireAsync(db, profiles, connector, declaration, ct));
        if (connection.CredentialProfileHash != bindingHash) throw new UnauthorizedAccessException("The connection settings changed. Reconnect before authorizing an agent.");
        var granted = List<string>(grant.CapabilitiesJson).Intersect(List<string>(requester.Grant!.RequiredCapabilitiesJson), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var required = consumer.Requires.Where(x => x.Dependency == dependency.Id && granted.Contains(x.Name)).Where(x => manifest.ProviderOperations.Any(o => o.Capability == x.Name &&
            o.Http is { Bootstrap: false } mapping && !mapping.ScopeSets.SelectMany(id => declaration.ScopeSets.Single(s => s.Id == id).Scopes)
                .Except(List<string>(connection.GrantedScopesJson), StringComparer.Ordinal).Any())).ToArray();
        if (required.Length == 0) throw new UnauthorizedAccessException("No requested operations are currently granted and consented.");
        foreach (var capability in required)
        {
            var candidate = new AgentCapabilityBinding { Id = Guid.NewGuid(), OrganizationId = requester.BusinessId, RequesterInstallationId = requester.Id,
                ProviderInstallationId = installationId, Capability = capability.Name, DependencyId = dependency.Id, GrantRevision = requester.Grant.GrantRevision,
                ProviderPackageDigest = request.PackageDigest, PluginAccountBusinessGrantId = grant.Id, PluginAccountResourceId = resource.Id,
                PluginAccountConnectionId = connection.Id, ApprovedAt = DateTimeOffset.UtcNow, Origin = AgentCapabilityBindingOrigins.Explicit };
            await new PluginAccountAuthorityService(db).RequireAsync(organizationId, candidate, connector, manifest, ct);
        }
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var existing = await db.AgentCapabilityBindings.Where(x => x.RequesterInstallationId == requester.Id && x.DependencyId == dependency.Id).ToListAsync(ct);
        await ConnectorStandingPolicyService.RevokeForConsumersAsync(db, organizationId, [requester.Id], ct);
        foreach (var requirement in required)
        {
            var binding = existing.SingleOrDefault(x => x.Capability == requirement.Name);
            if (binding is null) { binding = new() { Id = Guid.NewGuid(), RequesterInstallationId = requester.Id, OrganizationId = requester.BusinessId, Capability = requirement.Name }; db.AgentCapabilityBindings.Add(binding); }
            binding.ProviderInstallationId = installationId; binding.DependencyId = dependency.Id; binding.ProviderPackageDigest = request.PackageDigest;
            binding.GrantRevision = requester.Grant!.GrantRevision; binding.PluginAccountBusinessGrantId = grant.Id;
            binding.PluginAccountResourceId = resource.Id; binding.PluginAccountConnectionId = connection.Id;
            binding.ApprovedAt = DateTimeOffset.UtcNow; binding.RevokedAt = null; binding.Origin = AgentCapabilityBindingOrigins.Explicit;
        }
        foreach (var removed in existing.Where(x => !required.Any(r => r.Name == x.Capability))) removed.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
    }
}
