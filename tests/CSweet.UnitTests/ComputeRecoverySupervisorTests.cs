using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeRecoverySupervisorTests
{
    [Fact]
    public async Task Recovery_failure_signals_owner_without_stopping_local_lease_enforcement()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true); var executor = f.Executor();
        await executor.ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = row.Result.ExpiresAt;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = executor.CreateLeaseMonitor(f.Journal.Core.Time, pass => { if (pass.Attempted > 0) cleaned.TrySetResult(); });
        var maintenance = executor.CreateMaintenanceDeliveryWorker((_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => { });
        var recovery = executor.CreateObservationRecoveryWorker((_, _) => throw new UnauthorizedAccessException("Invalid Core authority."), f.Journal.Core.Time, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases, maintenance, recovery: recovery);
        var run = supervisor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(ComputeMaintenanceServiceFailure.RecoveryStopped, await supervisor.ObservationRecoveryFailure.WaitAsync(stop.Token));
            Assert.False(run.IsCompleted);
            f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1); leases.NotifyStateChanged();
            await cleaned.Task.WaitAsync(stop.Token); Assert.Single(f.Runner.Actions.Skip(1));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task Shutdown_joins_inflight_recovery_and_retains_pending_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor(); await executor.ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = row.Result.ExpiresAt;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = executor.CreateObservationRecoveryWorker(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
            return null;
        }, f.Journal.Core.Time, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(executor.CreateLeaseMonitor(f.Journal.Core.Time, _ => { }),
            executor.CreateMaintenanceDeliveryWorker((_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => { }), recovery: recovery);
        var run = supervisor.RunAsync(stop.Token);
        try { await entered.Task.WaitAsync(stop.Token); }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.True(exited.Task.IsCompleted); Assert.True(supervisor.ObservationRecoveryFailure.IsCanceled);
        Assert.Equal(row, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)));
    }
}
