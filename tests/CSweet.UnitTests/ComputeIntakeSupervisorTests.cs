using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeIntakeSupervisorTests
{
    [Fact]
    public async Task Intake_failure_does_not_stop_expired_workload_cleanup()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor();
        await executor.ExecuteAsync(f.Packet, default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = executor.CreateLeaseMonitor(f.Journal.Core.Time, pass => { if (pass.Attempted > 0) cleaned.TrySetResult(); });
        var intake = new ComputeDispatchIntakeWorker(new(f.Verifier),
            (_, _) => throw new UnauthorizedAccessException(), (_, _) => throw new Exception("No claim"),
            (_, _) => throw new Exception("No execution"), f.Journal.Core.Time, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases,
            executor.CreateMaintenanceDeliveryWorker((_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => { }), intake: intake);
        var run = supervisor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(ComputeMaintenanceServiceFailure.IntakeStopped, await supervisor.DispatchIntakeFailure.WaitAsync(stop.Token));
            Assert.False(run.IsCompleted);
            f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1); leases.NotifyStateChanged();
            await cleaned.Task.WaitAsync(stop.Token);
            Assert.Equal(InfrastructureActions.Destroy, Assert.Single(f.Runner.Actions.Skip(1)));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task Shutdown_joins_inflight_intake_transport()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intake = new ComputeDispatchIntakeWorker(new(f.Verifier), async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
            return new([], null);
        }, (_, _) => throw new Exception("No claim"), (_, _) => throw new Exception("No execution"), f.Journal.Core.Time, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(executor.CreateLeaseMonitor(f.Journal.Core.Time, _ => { }),
            executor.CreateMaintenanceDeliveryWorker((_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => { }), intake: intake);
        var run = supervisor.RunAsync(stop.Token);
        try { await entered.Task.WaitAsync(stop.Token); }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.True(exited.Task.IsCompleted); Assert.True(supervisor.DispatchIntakeFailure.IsCanceled);
    }

    [Fact]
    public async Task Coalesced_hint_wakes_idle_discovery_and_transport_failure_reports_backoff()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource<ComputeDispatchWorkerPass>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var intake = new ComputeDispatchIntakeWorker(new(f.Verifier), (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) throw new IOException("Offline");
            return Task.FromResult(new ComputeProviderWorkPage([], null));
        }, (_, _) => throw new Exception("No claim"), (_, _) => throw new Exception("No execution"), f.Journal.Core.Time,
            pass => { if (pass.ConsecutiveFailures == 0) initial.TrySetResult(); else retried.TrySetResult(pass); });
        var run = intake.RunAsync(stop.Token);
        try
        {
            await initial.Task.WaitAsync(stop.Token); intake.NotifyWorkAvailable();
            var pass = await retried.Task.WaitAsync(stop.Token);
            Assert.Null(pass.Intake); Assert.Equal(1, pass.ConsecutiveFailures); Assert.Equal(TimeSpan.FromSeconds(1), pass.NextDelay);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }
}
