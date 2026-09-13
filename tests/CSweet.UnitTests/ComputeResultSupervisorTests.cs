using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeResultSupervisorTests
{
    [Fact]
    public async Task Result_lane_failure_does_not_stop_lease_cleanup_or_maintenance_delivery()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true);
        var executor = f.Executor(); await executor.ExecuteAsync(f.Packet, default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = executor.CreateLeaseMonitor(f.Journal.Core.Time, pass => { if (pass.Attempted > 0) cleaned.TrySetResult(); });
        var maintenance = executor.CreateMaintenanceDeliveryWorker((_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => { });
        var results = executor.CreateResultDeliveryWorker((_, _) => throw new InvalidOperationException("Fatal result configuration."), f.Journal.Core.Time, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases, maintenance, results);
        var run = supervisor.RunAsync(stop.Token);
        try
        {
            await supervisor.ResultDeliveryFailure.WaitAsync(stop.Token);
            Assert.False(run.IsCompleted); Assert.False(supervisor.DeliveryFailure.IsCompleted);
            f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1); leases.NotifyStateChanged();
            await cleaned.Task.WaitAsync(stop.Token);
            Assert.Single(f.Runner.Actions.Skip(1));
            Assert.Single(await f.Journal.Journal().ListResultsAsync(100, stop.Token));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task Executor_wakes_result_delivery_and_supervisor_joins_cancelled_delivery()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = executor.CreateResultDeliveryWorker(async (row, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
            return new ComputeResultAcknowledgement(row.Result.OperationId, row.Result.Sequence, row.Digest, false);
        }, f.Journal.Core.Time, _ => initial.TrySetResult());
        var supervisor = new ComputeProviderMaintenanceSupervisor(executor.CreateLeaseMonitor(f.Journal.Core.Time, _ => { }),
            executor.CreateMaintenanceDeliveryWorker((_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => { }), results);
        var run = supervisor.RunAsync(stop.Token);
        try
        {
            await initial.Task.WaitAsync(stop.Token);
            await executor.ExecuteAsync(f.Packet, stop.Token);
            await entered.Task.WaitAsync(stop.Token);
            Assert.Throws<InvalidOperationException>(() => executor.CreateResultDeliveryWorker((_, _) => throw new InvalidOperationException(), f.Journal.Core.Time, _ => { }));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.True(exited.Task.IsCompleted); Assert.True(supervisor.ResultDeliveryFailure.IsCanceled);
        Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
    }
}
