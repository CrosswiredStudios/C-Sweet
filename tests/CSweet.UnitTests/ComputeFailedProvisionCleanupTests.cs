using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeFailedProvisionCleanupTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Failed_provision_cleanup_is_grant_checked_idempotent_and_preserves_persistent_instances(
        bool persistent, bool revoked, bool expected)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await f.Send();
        var environment = await f.Db.ComputeEnvironments.SingleAsync();
        environment.State = ComputeLifecycleState.Failed;
        if (persistent) environment.Persistence = ComputePersistence.Persistent;
        if (revoked) f.Db.ScopedActionGrants.Remove(await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Destroy));
        await f.Db.SaveChangesAsync();
        var cleanup = new ComputeFailedProvisionCleanup(f.Db, f.Broker);
        await cleanup.RunOnceAsync(default); await cleanup.RunOnceAsync(default);
        f.Db.ChangeTracker.Clear();
        environment = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(expected ? ComputeDesiredState.Destroyed : ComputeDesiredState.Running, environment.DesiredState);
        Assert.Equal(expected ? 1 : 0, await f.Db.ComputeOperations.CountAsync(x => x.Action == InfrastructureActions.Destroy));
        Assert.Null(environment.TeardownConfirmedAt); // Quota releases only after the provider proves removal.
    }
}
