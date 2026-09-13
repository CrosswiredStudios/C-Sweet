using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeLeaseMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_recovers_expired_leases_without_a_notification_or_core_connection(bool persistent)
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent);
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ComputeLeaseMonitorPass? observed = null;
        var monitor = f.Executor().CreateLeaseMonitor(f.Journal.Core.Time, pass => { observed = pass; cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cancellation.Token));
        Assert.NotNull(observed); Assert.Equal(1, observed.Attempted); Assert.Equal(0, observed.Failed);
        Assert.Equal(persistent ? InfrastructureActions.Stop : InfrastructureActions.Destroy, Assert.Single(f.Runner.Actions.Skip(1)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wake_hints_and_timer_recovery_both_read_current_persisted_leases(bool sendWake)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var executor = f.Executor();
        var monitor = new ComputeLeaseMonitor(f.Journal.Journal(), executor.EnforceExpiredLeaseAsync, f.Journal.Core.Time, pass =>
        {
            first.TrySetResult();
            if (pass.Attempted > 0) { cleaned = pass.Failed == 0; cancellation.Cancel(); }
        }, sendWake ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            await first.Task.WaitAsync(cancellation.Token);
            Assert.Empty(f.Runner.Actions.Skip(1));
            f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
            if (sendWake) for (var i = 0; i < 1000; i++) monitor.NotifyStateChanged();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.True(cleaned);
            Assert.Equal(InfrastructureActions.Destroy, Assert.Single(f.Runner.Actions.Skip(1)));
        }
        finally { cancellation.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task A_failed_workload_does_not_prevent_other_due_leases_from_being_enforced()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        var second = f.Claim with { EnvironmentId = Guid.NewGuid(), OperationId = Guid.NewGuid(), DispatchId = Guid.NewGuid() };
        var packet = f.Packet with { Authorization = await f.Journal.Core.Signing.SignAsync(second, default) };
        await f.Journal.Journal().RunAsync(f.Verifier.Verify(packet), (decision, _) => Task.FromResult(decision), default);
        var failingId = f.Claim.EnvironmentId.CompareTo(second.EnvironmentId) < 0 ? f.Claim.EnvironmentId : second.EnvironmentId;
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var visited = new List<Guid>(); ComputeLeaseMonitorPass? observed = null;
        var monitor = new ComputeLeaseMonitor(f.Journal.Journal(), (id, token) =>
            f.Journal.Journal().EnforceExpiredLeaseAsync(id, (authority, _) =>
            {
                visited.Add(id);
                if (id == failingId) throw new IOException("Unknown physical outcome.");
                return Task.FromResult(new ComputePhysicalOutcome<bool>(true, authority.ResourceId));
            }, token), f.Journal.Core.Time, pass => { observed = pass; cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cancellation.Token));
        Assert.Equal(2, visited.Count); Assert.Equal(failingId, visited[0]);
        Assert.NotNull(observed); Assert.Equal(2, observed.Attempted); Assert.Equal(1, observed.Failed);
        Assert.Equal(failingId, Assert.Single(observed.FailedEnvironmentIds));
        Assert.All(await f.Journal.Journal().ListReservationsAsync(null, 100, default), x => Assert.True(x.LeaseExpired));
    }

    [Fact]
    public async Task Missing_journal_faults_the_monitor_without_initializing_or_executing()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var path = Path.Combine(f.Journal.Root, "journal.json"); File.Delete(path);
        var calls = 0;
        var monitor = new ComputeLeaseMonitor(f.Journal.Journal(), (_, _) => { calls++; return Task.FromResult(true); }, f.Journal.Core.Time, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => monitor.RunAsync(default));
        Assert.Equal(0, calls); Assert.False(File.Exists(path));
    }
}
