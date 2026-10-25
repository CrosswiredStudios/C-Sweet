using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    public async Task SaveGrantAsync(Guid userId, Guid installationId, Guid accountId, SavePluginAccountGrantRequest request, CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(userId, ct);
        var (installation, manifest, _) = await Context(installationId, ct);
        if (request.PackageDigest != installation.PackageVersion!.PackageDigest) throw new InvalidOperationException("The plugin changed; review access again.");
        var account = await db.PluginAccountGroups.SingleOrDefaultAsync(x => x.Id == accountId && x.PluginInstallationId == installationId && x.State == PluginAccountState.Connected, ct)
            ?? throw new UnauthorizedAccessException("The account is unavailable.");
        await db.Entry(account).ReloadAsync(ct);
        if (account.State != PluginAccountState.Connected || !await db.CoreOrganizations.AnyAsync(x => x.Id == request.OrganizationId, ct)) throw new UnauthorizedAccessException("The account or business is unavailable.");
        if (request.PermissionIds is null || request.PermissionIds.Count is < 1 or > 64 || request.ResourceIds is null || request.ResourceIds.Count > manifest.Accounts!.MaximumResources ||
            request.PermissionIds.Distinct().Count() != request.PermissionIds.Count || request.ResourceIds.Distinct().Count() != request.ResourceIds.Count)
            throw new ArgumentException("Choose the business permissions and resources to share.");
        var permissions = manifest.Accounts!.Permissions.Where(x => request.PermissionIds.Contains(x.Id)).ToArray();
        if (permissions.Length != request.PermissionIds.Count) throw new ArgumentException("Choose only declared permission groups.");
        var scope = request.Scope switch { "WholeAccount" => PluginAccountGrantScope.WholeAccount, "SelectedResources" => PluginAccountGrantScope.SelectedResources,
            _ => throw new ArgumentException("Choose account-wide or selected-resource access.") };
        if (scope == PluginAccountGrantScope.WholeAccount && (!request.IncludeFutureResources || request.ResourceIds.Count != 0) ||
            scope == PluginAccountGrantScope.SelectedResources && (request.IncludeFutureResources || request.ResourceIds.Count == 0 ||
                await db.PluginAccountResources.CountAsync(x => x.AccountGroupId == accountId && x.PluginInstallationId == installationId && x.IsAvailable && request.ResourceIds.Contains(x.Id), ct) != request.ResourceIds.Count))
            throw new ArgumentException("Account-wide access requires future-resource acknowledgement; otherwise select available resources explicitly.");
        var capabilities = permissions.SelectMany(x => x.Capabilities).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (capabilities.Except(List<string>(installation.Grant!.ProvidedCapabilitiesJson), StringComparer.Ordinal).Any())
            throw new UnauthorizedAccessException("This plugin build has not been granted these abilities.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var grant = await db.PluginAccountBusinessGrants.SingleOrDefaultAsync(x => x.PluginInstallationId == installationId && x.AccountGroupId == accountId && x.OrganizationId == request.OrganizationId, ct);
        if (request.ExpectedRevision != grant?.Revision) throw new InvalidOperationException("Business access changed after you opened it. Refresh and review again.");
        var now = DateTimeOffset.UtcNow;
        if (grant is null)
        {
            grant = new() { Id = Guid.NewGuid(), PluginInstallationId = installationId, AccountGroupId = accountId, OrganizationId = request.OrganizationId, CreatedAt = now };
            db.PluginAccountBusinessGrants.Add(grant);
        }
        else await InvalidateGrant(grant, ct);
        grant.Scope = scope; grant.SelectedResourceIdsJson = JsonSerializer.Serialize(request.ResourceIds.Order());
        grant.CapabilitiesJson = JsonSerializer.Serialize(capabilities); grant.PackageDigest = request.PackageDigest;
        grant.PolicyVersion = manifest.Accounts.PolicyVersion; grant.ApprovedByApplicationUserId = userId;
        grant.FutureResourcesAcknowledgedAt = scope == PluginAccountGrantScope.WholeAccount ? now : null;
        grant.RevokedAt = null; grant.UpdatedAt = now; grant.Revision++;
        // Participate in the account's concurrency fence: a concurrent disconnect cannot commit a fresh grant.
        account.Revision++; account.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin-account.business-access.saved", nameof(PluginAccountBusinessGrant), grant.Id,
            "An enterprise administrator explicitly assigned business account/resource permissions. Consumer bindings require separate authorization.", cancellationToken: ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    public async Task RevokeGrantAsync(Guid userId, Guid installationId, Guid grantId, long expectedRevision, CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(userId, ct);
        var grant = await db.PluginAccountBusinessGrants.SingleOrDefaultAsync(x => x.Id == grantId && x.PluginInstallationId == installationId, ct)
            ?? throw new UnauthorizedAccessException("Business access was not found.");
        if (grant.Revision != expectedRevision) throw new InvalidOperationException("Business access changed. Refresh and review again.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await InvalidateGrant(grant, ct); grant.RevokedAt = DateTimeOffset.UtcNow; grant.UpdatedAt = DateTimeOffset.UtcNow; grant.Revision++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin-account.business-access.revoked", nameof(PluginAccountBusinessGrant), grant.Id,
            "An administrator removed business/consumer access without revoking shared provider credentials.", cancellationToken: ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    private async Task InvalidateGrant(PluginAccountBusinessGrant grant, CancellationToken ct)
    {
        var bindings = await db.AgentCapabilityBindings.Where(x => x.PluginAccountBusinessGrantId == grant.Id && x.RevokedAt == null).ToArrayAsync(ct);
        foreach (var binding in bindings) binding.RevokedAt = DateTimeOffset.UtcNow;
        await ConnectorStandingPolicyService.RevokeForConsumersAsync(db, grant.OrganizationId, bindings.Select(x => x.RequesterInstallationId).Distinct().ToArray(), ct);
        var policies = await db.PluginStandingPolicies.Where(x => x.OrganizationId == grant.OrganizationId && bindings.Select(b => b.RequesterInstallationId).Contains(x.AgentInstallationId) &&
            x.Status == PluginStandingPolicyStatus.Approved).ToArrayAsync(ct);
        foreach (var policy in policies) { policy.Status = PluginStandingPolicyStatus.Revoked; policy.RevokedAt = DateTimeOffset.UtcNow; }
    }
    public async Task<IReadOnlyList<PluginManagedAccount>> GetBusinessAccountsAsync(Guid userId, Guid organizationId, Guid installationId, CancellationToken ct)
    {
        if (!await Access.IsActiveHumanAsync(userId, ct) || !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId && x.ApplicationUserId == userId && x.IsActive &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel >= OrganizationPermissionLevel.Manager, ct)) throw new UnauthorizedAccessException("Business management access is required.");
        var (_, manifest, _) = await Context(installationId, ct);
        var grants = await db.PluginAccountBusinessGrants.AsNoTracking().Where(x => x.PluginInstallationId == installationId && x.OrganizationId == organizationId && x.RevokedAt == null).ToArrayAsync(ct);
        var result = new List<PluginManagedAccount>();
        foreach (var grant in grants)
        {
            var selected = List<Guid>(grant.SelectedResourceIdsJson);
            var group = await db.PluginAccountGroups.AsNoTracking().SingleAsync(x => x.Id == grant.AccountGroupId, ct);
            var resources = await db.PluginAccountResources.AsNoTracking().Where(x => x.AccountGroupId == group.Id && x.PluginInstallationId == installationId &&
                (grant.Scope == PluginAccountGrantScope.WholeAccount && grant.FutureResourcesAcknowledgedAt != null || selected.Contains(x.Id)))
                .Select(x => new PluginManagedResource(x.Id, x.ExternalResourceId, x.DisplayName, x.Handle, x.IsAvailable)).ToArrayAsync(ct);
            result.Add(new(group.Id, grant.Scope == PluginAccountGrantScope.WholeAccount ? group.DisplayName : manifest.Accounts!.AccountLabel,
                group.State.ToString(), group.Revision, false, resources, [], [new(grant.Id, organizationId, grant.Scope.ToString(), selected,
                    manifest.Accounts!.Permissions.Where(p => p.Capabilities.All(c => List<string>(grant.CapabilitiesJson).Contains(c))).Select(p => p.Id).ToArray(), grant.Revision)]));
        }
        return result;
    }
}
