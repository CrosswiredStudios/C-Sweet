using CSweet.Compute.Contracts;
using CSweet.Application.Compute;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeLifecycleTests
{
    [Fact]
    public async Task Destroy_fences_pending_provision_without_claiming_physical_teardown_and_replays_after_restart()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var initial = await f.Send();
        var request = new ChangeComputeLifecycle(initial.Id, initial.Generation, InfrastructureActions.Destroy, "destroy-1");
        var destroyed = await f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation, request, default);
        Assert.Equal(2, destroyed.Generation);
        Assert.Equal(ComputeDesiredState.Destroyed, destroyed.DesiredState);
        Assert.Equal(ComputeLifecycleState.Requested, destroyed.State);
        var environment = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Null(environment.TeardownConfirmedAt);
        Assert.True(environment.HoldsReservation);
        Assert.Equal("Superseded", (await f.Db.ComputeOperations.SingleAsync(x => x.Action == InfrastructureActions.Provision)).Status);
        await using var restarted = new CSweetDbContext(f.Options);
        var broker = new ComputeBroker(restarted, f.Templates, new ComputeBrokerTests.Clock(), f.Ledger);
        Assert.Equal(destroyed, await broker.ChangeLifecycleAsync(f.Organization, f.Installation, request, default));
        Assert.Equal(2, await restarted.ComputeOperations.CountAsync());
        Assert.Equal(2, await restarted.ComputeProviderWakes.CountAsync());
        Assert.Equal(2, await restarted.ComputeAuditOutbox.CountAsync());
        Assert.Equal(2, await restarted.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.changed.v1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            request with { Action = InfrastructureActions.Start }, default));
    }

    [Theory]
    [InlineData(InfrastructureActions.Start, ComputeLifecycleState.Stopped, ComputeDesiredState.Running)]
    [InlineData(InfrastructureActions.Stop, ComputeLifecycleState.Ready, ComputeDesiredState.Stopped)]
    [InlineData(InfrastructureActions.Restart, ComputeLifecycleState.Ready, ComputeDesiredState.Running)]
    public async Task Lifecycle_records_new_generation_with_current_authority(string action, ComputeLifecycleState state, ComputeDesiredState desired)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var initial = await f.Send();
        await ObserveAsync(f, state);
        var result = await f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation, new(initial.Id, 1, action, "lifecycle"), default);
        Assert.Equal(desired, result.DesiredState);
        Assert.Equal(state, result.State);
        Assert.Equal(2, result.Generation);
        var operation = await f.Db.ComputeOperations.SingleAsync(x => x.Action == action);
        Assert.Equal("Pending", operation.Status);
        Assert.Contains(action, operation.AuthorityJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(initial.Id, 1, action, "stale-trigger"), default));
    }

    [Fact]
    public async Task Expired_lease_blocks_activation_but_allows_explicit_destruction()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var initial = await f.Send();
        await ObserveAsync(f, ComputeLifecycleState.Stopped);
        var environment = await f.Db.ComputeEnvironments.SingleAsync(); environment.LeaseExpiresAt = initial.CreatedAt.AddSeconds(-1);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(initial.Id, 1, InfrastructureActions.Start, "start-expired"), default));
        var result = await f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(initial.Id, 1, InfrastructureActions.Destroy, "destroy-expired"), default);
        Assert.Equal(ComputeDesiredState.Destroyed, result.DesiredState);
    }

    [Fact]
    public async Task Revoked_action_grant_blocks_replays_and_new_actions()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var initial = await f.Send();
        var request = new ChangeComputeLifecycle(initial.Id, 1, InfrastructureActions.Destroy, "destroy");
        await f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation, request, default);
        var grant = await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Destroy);
        grant.RevokedAt = initial.CreatedAt; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation, request, default));
    }

    [Fact]
    public async Task Activation_requires_independent_network_authority_and_cannot_overlap_provisioning()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var initial = await f.Send();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(initial.Id, 1, InfrastructureActions.Stop, "overlap"), default));
        await ObserveAsync(f, ComputeLifecycleState.Stopped);
        var environment = await f.Db.ComputeEnvironments.SingleAsync();
        var networked = f.Request.Specification with { Network = new(ComputeNetworkMode.OutboundOnly, AllowOutbound: true) };
        environment.SpecificationJson = System.Text.Json.JsonSerializer.Serialize(networked, ComputeBroker.Json);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(initial.Id, 1, InfrastructureActions.Start, "network-start"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ChangeLifecycleAsync(Guid.NewGuid(), f.Installation,
            new(initial.Id, 1, InfrastructureActions.Destroy, "cross-organization"), default));
        Assert.Equal(1, await f.Db.ComputeOperations.CountAsync());
    }

    private static async Task ObserveAsync(ComputeBrokerTests.Fixture fixture, ComputeLifecycleState state)
    {
        var operation = await fixture.Db.ComputeOperations.SingleAsync(); operation.Status = "Completed";
        var environment = await fixture.Db.ComputeEnvironments.SingleAsync(); environment.State = state;
        if (state == ComputeLifecycleState.Stopped) environment.DesiredState = ComputeDesiredState.Stopped;
        await fixture.Db.SaveChangesAsync();
    }
}
