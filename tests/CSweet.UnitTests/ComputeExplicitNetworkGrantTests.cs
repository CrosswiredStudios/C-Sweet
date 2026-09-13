using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeExplicitNetworkGrantTests
{
    [Fact]
    public async Task Only_owner_can_grant_exact_instance_local_access_and_revocation_emits_a_wake()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var view = await f.Send();
        var resource = await f.Db.ComputeEnvironments.SingleAsync(); resource.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var approval = await f.Db.AgentInstallationGrants.SingleAsync();
        approval.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { InfrastructureActions.Inbound, InfrastructureActions.PublishPort });
        var user = Guid.NewGuid(); var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Organization, ApplicationUserId = user,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true };
        f.Db.CoreOrganizationUsers.Add(owner); await f.Db.SaveChangesAsync();
        var service = new ComputeGrantAdministration(f.Db, TimeProvider.System, new AuditExecutionContextAccessor());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeNetworkGrantEndpoints.SetLocalLinkAsync(f.Db, service,
            f.Organization, user, view.Id, true, default));
        owner.PermissionLevel = OrganizationPermissionLevel.Owner; await f.Db.SaveChangesAsync();
        await ComputeNetworkGrantEndpoints.SetLocalLinkAsync(f.Db, service, f.Organization, user, view.Id, true, default);
        var grants = await f.Db.ScopedActionGrants.Where(x => x.Action.StartsWith("network.")).ToListAsync();
        Assert.Equal(2, grants.Count);
        foreach (var grant in grants)
        {
            var limits = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
            Assert.Equal(view.Id, limits.EnvironmentId); Assert.Equal(new HashSet<int> { 8080 }, limits.AllowedPublishedPorts);
            Assert.False(limits.AllowOutbound); Assert.False(limits.AllowPublicEndpoint);
            Assert.Equal(resource.LeaseExpiresAt, grant.ExpiresAt);
        }
        await ComputeNetworkGrantEndpoints.SetLocalLinkAsync(f.Db, service, f.Organization, user, view.Id, false, default);
        Assert.All(grants, x => Assert.NotNull(x.RevokedAt));
        Assert.Equal(2, await f.Db.AgentPlatformEventOutbox.CountAsync(x => x.IdempotencyKey.StartsWith("network-grant:")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeNetworkGrantEndpoints.SetLocalLinkAsync(f.Db, service,
            Guid.NewGuid(), user, view.Id, true, default));
    }

    [Fact]
    public void Public_exposure_requires_its_own_action_and_instance_grants_cannot_provision()
    {
        var spec = new CSweet.Domain.Compute.ComputeSpecification("linux", "x64", "ubuntu-clean", new(1, 1024, 20480), 600,
            Network: new(CSweet.Domain.Compute.ComputeNetworkMode.Inbound, PublicEndpoint: true, PublishedPorts: [8080]));
        Assert.Contains(InfrastructureActions.PublicEndpoint, ComputePolicy.RequiredProvisionActions(spec));
        Assert.DoesNotContain(InfrastructureActions.Outbound, ComputePolicy.RequiredProvisionActions(spec));
        var constraints = new ComputeGrantConstraints(1, spec.Resources, 1, 600, ["linux"], ["x64"], ["ubuntu-clean"],
            AllowedPublishedPorts: [8080], AllowPublicEndpoint: true, EnvironmentId: Guid.NewGuid());
        Assert.False(ComputePolicy.Evaluate(spec, new("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), []),
            constraints, [], 0, DateTimeOffset.UtcNow).Allowed);
    }
}
