using CSweet.Compute.Contracts;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeLeaseEnforcementTests
{
    [Fact]
    public async Task Until_released_reservation_survives_idle_time_and_provider_reopen()
    {
        await using var f = new Fixture(); await f.InitializeAsync(untilReleased: true);
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Journal.Core.Time.Now.AddYears(1);
        Assert.False(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.True(f.Runner.Exists);
        Assert.Single(f.Runner.Actions);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).LeaseExpiresAt);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_uses_original_persistence_terms_and_preserves_storage_without_new_core_authority(bool persistent)
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent);
        await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.False(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Empty(f.Runner.Actions.Skip(1));
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        Assert.True(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Equal(persistent ? InfrastructureActions.Stop : InfrastructureActions.Destroy, Assert.Single(f.Runner.Actions.Skip(1)));
        Assert.Equal(persistent, f.Runner.Exists);
        var reservation = Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
        Assert.True(reservation.LeaseExpired); Assert.Equal(!persistent, reservation.DestroyRequested);
        Assert.Equal(persistent, File.Exists(Path.Combine(f.Workloads, f.Claim.EnvironmentId.ToString("N"), "os.vhdx")));
        // Restart recovery verifies exact-owned state without another mutation or audit pair.
        Assert.False(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Single(f.Runner.Actions.Skip(1));
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_cleanup_and_clock_rollback_cannot_reactivate_an_expired_environment(bool persistent)
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent);
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        f.Runner.LoseControlResponse = true;
        await Assert.ThrowsAsync<IOException>(() => f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.True(Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).LeaseExpired);
        f.Journal.Core.Time.Now = f.Claim.IssuedAt.AddSeconds(1);
        var start = await f.Control(InfrastructureActions.Start, 2);
        var before = f.Runner.Actions.Count;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Executor().ExecuteAsync(start, default));
        Assert.Equal(before, f.Runner.Actions.Count);
        f.Runner.LoseControlResponse = false;
        Assert.True(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Equal(persistent ? InfrastructureActions.Stop : InfrastructureActions.Destroy, f.Runner.Actions.Last());
    }

    [Fact]
    public async Task Unknown_environment_has_no_local_cleanup_authority()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        Assert.False(await f.Executor().EnforceExpiredLeaseAsync(Guid.NewGuid(), default));
        Assert.Empty(f.Runner.Scripts);
    }
}
