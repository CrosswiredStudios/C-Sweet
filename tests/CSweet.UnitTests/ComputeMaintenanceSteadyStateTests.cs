using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceSteadyStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_cleanup_recovery_reads_physical_state_without_growing_offline_evidence(bool persistent)
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent); await ExpireAsync(f);
        var before = await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json"));
        for (var i = 0; i < 20; i++)
        {
            f.Journal.Core.Time.Now = f.Journal.Core.Time.Now.AddMinutes(1);
            Assert.False(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        }
        Assert.Single(f.Runner.Actions.Skip(1));
        Assert.Equal(20, f.Runner.Scripts.Count(x => x == HyperVComputeDriver.ObserveScript));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json")));
        Assert.Equal(2, (await f.Journal.Journal().ListMaintenanceEventsAsync(100, default)).Count);
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Physical_drift_triggers_new_attributed_cleanup_and_a_failed_attempt_invalidates_old_confirmation()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true); await ExpireAsync(f);
        f.Runner.PhysicalState = "Running";
        f.Runner.LoseControlResponse = true;
        await Assert.ThrowsAsync<IOException>(() => f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        // The old confirmation cannot suppress recovery of the newer failed attempt, even at the same clock time.
        f.Runner.PhysicalState = "Off";
        f.Runner.LoseControlResponse = false;
        Assert.True(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Equal(4, f.Runner.Actions.Count);
        var events = (await f.Journal.Journal().ListMaintenanceEventsAsync(100, default))
            .Select(row => JsonSerializer.Deserialize<ComputeMaintenanceEvent>(row.EventJson, ComputeProtocol.Json)!).ToArray();
        Assert.Equal(6, events.Length);
        Assert.Equal(new long[] { 1, 2, 3 }, events.Where(x => x.Phase == ComputeMaintenancePhase.Requested)
            .Select(x => x.Attempt).Order());
        Assert.Single(events, x => x.Phase == ComputeMaintenancePhase.Failed && x.Attempt == 2);
        Assert.False(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
    }

    [Fact]
    public async Task Inventory_failure_is_not_confirmation_and_keeps_the_existing_journal_unchanged()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true); await ExpireAsync(f);
        var before = await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json"));
        f.Runner.FailObservation = true;
        await Assert.ThrowsAsync<IOException>(() => f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Single(f.Runner.Actions.Skip(1));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json")));
        f.Runner.FailObservation = false;
        f.Runner.PhysicalState = "Running";
        Assert.True(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
        Assert.Equal(3, f.Runner.Actions.Count);
        Assert.Equal("Off", f.Runner.PhysicalState);
    }

    private static async Task ExpireAsync(Fixture f)
    {
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        Assert.True(await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default));
    }
}
