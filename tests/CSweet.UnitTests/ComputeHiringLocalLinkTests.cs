using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Compute;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeHiringLocalLinkTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Local_links_require_both_manifest_declaration_and_hiring_approval(bool declared, bool approved, bool expected)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        await SetPermission(f, declared, approved);
        var view = await f.Send();
        var grants = await f.Db.ScopedActionGrants.Where(x => x.Action.StartsWith("network.")).ToListAsync();
        Assert.Equal(expected ? 2 : 0, grants.Count);
        foreach (var grant in grants)
        {
            Assert.True(ComputeLocalLinkGrants.IsHiringGrant(grant));
            var limits = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
            Assert.Equal(view.Id, limits.EnvironmentId);
            Assert.Equal(new HashSet<int> { 8080 }, limits.AllowedPublishedPorts);
            Assert.Equal(view.LeaseExpiresAt, grant.ExpiresAt);
            Assert.False(limits.AllowOutbound); Assert.False(limits.AllowPublicEndpoint);
        }
        await f.Send();
        await f.Broker.ReadAsync(f.Organization, f.Installation, view.Id, default);
        Assert.Equal(expected ? 1 : 0, await f.Db.AuditOutbox.CountAsync(x => x.RequestJson.Contains("compute.local-test-links.granted.v1")));
        Assert.Equal(expected ? 1 : 0, await f.Db.AgentPlatformEventOutbox.CountAsync(x => x.IdempotencyKey.StartsWith("hired-local-link:")));
    }

    [Fact]
    public async Task Existing_instance_recovers_permission_and_revoked_or_edited_grants_are_preserved()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var view = await f.Send();
        await SetPermission(f, true, true);
        await f.Broker.ReadAsync(f.Organization, f.Installation, view.Id, default);
        var grants = await f.Db.ScopedActionGrants.Where(x => x.Action.StartsWith("network.")).OrderBy(x => x.Action).ToListAsync();
        Assert.Equal(2, grants.Count);
        grants[0].RevokedAt = new ComputeBrokerTests.Clock().GetUtcNow(); grants[0].Revision++;
        grants[1].ConstraintsJson = "{}"; grants[1].Revision++;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await f.Send();
        await f.Broker.ReadAsync(f.Organization, f.Installation, view.Id, default);
        var retained = await f.Db.ScopedActionGrants.Where(x => x.Action.StartsWith("network.")).OrderBy(x => x.Action).ToListAsync();
        Assert.NotNull(retained[0].RevokedAt); Assert.Equal("{}", retained[1].ConstraintsJson);
        Assert.All(retained, x => Assert.Equal(2, x.Revision));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Withdrawing_approval_or_manifest_permission_immediately_removes_derived_authority(bool retainDeclaration)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        await SetPermission(f, true, true); await f.Send();
        Assert.Equal(2, (await f.Broker.GrantsAsync(f.Organization, f.Installation, f.Workstream, default)).Count(ComputeLocalLinkGrants.IsHiringGrant));
        await SetPermission(f, retainDeclaration, !retainDeclaration);
        Assert.DoesNotContain(await f.Broker.GrantsAsync(f.Organization, f.Installation, f.Workstream, default), ComputeLocalLinkGrants.IsHiringGrant);
    }

    [Fact]
    public async Task Permission_changes_queue_durable_wakes_and_saving_unchanged_state_does_not_repeat_them()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await f.Send();
        await SetPermission(f, true, true);
        var wake = Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(x => x.IdempotencyKey.StartsWith("local-link-permission:")).ToListAsync());
        Assert.Equal(f.Installation, wake.TargetInstallationId);
        Assert.Contains(await f.Db.ApplicationRealtimeOutbox.ToListAsync(), x => x.EventType == AppRealtimeEvents.ComputeChanged);
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Db.AgentPlatformEventOutbox.CountAsync(x => x.IdempotencyKey.StartsWith("local-link-permission:")));
        await SetPermission(f, true, false);
        Assert.Equal(2, await f.Db.AgentPlatformEventOutbox.CountAsync(x => x.IdempotencyKey.StartsWith("local-link-permission:")));
    }

    [Fact]
    public async Task Approved_links_can_publish_8080_but_never_other_ports()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        await SetPermission(f, true, true);
        var view = await ComputeWorkloadTests.ReadyAsync(f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation,
            new(view.Id, 1, "wrong-port", new(PublishPort: 9090)), default));
        var accepted = await f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation,
            new(view.Id, 1, "local-link", new(PublishPort: 8080)), default);
        var operation = await f.Db.ComputeOperations.SingleAsync(x => x.Id == accepted.Id);
        Assert.Contains(InfrastructureActions.Inbound, operation.AuthorityJson);
        Assert.Contains(InfrastructureActions.PublishPort, operation.AuthorityJson);
    }

    private static async Task SetPermission(ComputeBrokerTests.Fixture f, bool declared, bool approved)
    {
        var installation = await f.Db.AgentInstallations.Include(x => x.Grant).Include(x => x.PackageVersion).SingleAsync();
        installation.PackageVersion!.ManifestJson = JsonSerializer.Serialize(new
        {
            requires = declared ? new[] { new { name = ComputePermissions.LocalTestLinks } } : []
        });
        var capabilities = JsonSerializer.Deserialize<HashSet<string>>(installation.Grant!.RequiredCapabilitiesJson)!;
        capabilities.UnionWith(ComputeLocalLinkGrants.Actions);
        if (approved) capabilities.Add(ComputePermissions.LocalTestLinks); else capabilities.Remove(ComputePermissions.LocalTestLinks);
        installation.Grant.RequiredCapabilitiesJson = JsonSerializer.Serialize(capabilities);
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
    }
}
