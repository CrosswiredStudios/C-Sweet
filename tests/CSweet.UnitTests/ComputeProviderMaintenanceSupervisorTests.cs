using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeProviderMaintenanceSupervisorTests
{
    [Fact]
    public async Task Fatal_delivery_failure_signals_owner_while_expiry_enforcement_continues()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true); await QueueAsync(f);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var afterFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureSeen = 0;
        var executor = f.Executor();
        var leases = executor.CreateLeaseMonitor(TimeProvider.System, pass =>
        {
            if (Volatile.Read(ref failureSeen) == 1 && pass.Attempted > 0) afterFailure.TrySetResult();
        });
        var delivery = executor.CreateMaintenanceDeliveryWorker((_, _) =>
            throw new InvalidOperationException("Signing credential unavailable; sensitive details stay local."), TimeProvider.System, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases, delivery);
        var running = supervisor.RunAsync(cancellation.Token);
        try
        {
            Assert.Equal(ComputeMaintenanceServiceFailure.DeliveryStopped,
                await supervisor.DeliveryFailure.WaitAsync(cancellation.Token));
            Assert.False(running.IsCompleted);
            Volatile.Write(ref failureSeen, 1); leases.NotifyStateChanged();
            await afterFailure.Task.WaitAsync(cancellation.Token);
            Assert.Single(f.Runner.Actions.Skip(1));
            Assert.Contains(CSweet.Compute.HyperV.HyperVComputeDriver.ObserveScript, f.Runner.Scripts);
            Assert.NotEmpty(await f.Journal.Journal().ListMaintenanceEventsAsync(100, cancellation.Token));
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
    }

    [Fact]
    public async Task Slow_delivery_does_not_block_cleanup_and_shutdown_waits_for_transport_exit()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true); await QueueAsync(f);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = f.Executor();
        var leases = executor.CreateLeaseMonitor(TimeProvider.System, pass =>
        { if (entered.Task.IsCompleted && pass.Attempted > 0) cleaned.TrySetResult(); });
        var delivery = executor.CreateMaintenanceDeliveryWorker(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
        }, TimeProvider.System, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases, delivery);
        var running = supervisor.RunAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(cancellation.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RunAsync(cancellation.Token));
            leases.NotifyStateChanged();
            await cleaned.Task.WaitAsync(cancellation.Token);
            Assert.False(exited.Task.IsCompleted);
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
        Assert.True(exited.Task.IsCompleted);
        Assert.True(supervisor.DeliveryFailure.IsCanceled);
        Assert.NotEmpty(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
    }

    [Fact]
    public async Task Lease_monitor_failure_stops_delivery_and_surfaces_to_the_service_owner()
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true); await QueueAsync(f);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = 0;
        var executor = f.Executor();
        var leases = executor.CreateLeaseMonitor(TimeProvider.System, _ =>
        { if (Volatile.Read(ref fail) == 1) throw new InvalidDataException("Monitor supervision failure."); });
        var delivery = executor.CreateMaintenanceDeliveryWorker(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
        }, TimeProvider.System, _ => { });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases, delivery);
        var running = supervisor.RunAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(cancellation.Token);
            Volatile.Write(ref fail, 1); leases.NotifyStateChanged();
            await Assert.ThrowsAsync<InvalidDataException>(() => running);
            Assert.True(exited.Task.IsCompleted);
            Assert.True(supervisor.DeliveryFailure.IsCanceled);
        }
        finally { cancellation.Cancel(); try { await running; } catch (InvalidDataException) { } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task Executor_wakes_delivery_after_cleanup_without_waiting_for_recovery_timer()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var executor = f.Executor();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        var worker = executor.CreateMaintenanceDeliveryWorker((_, _) =>
        { delivered++; return Task.CompletedTask; }, TimeProvider.System, pass =>
        {
            initial.TrySetResult();
            if (pass.Delivered > 0) cancellation.Cancel();
        });
        var running = worker.RunAsync(cancellation.Token);
        try
        {
            await initial.Task.WaitAsync(cancellation.Token);
            Assert.Throws<InvalidOperationException>(() => executor.CreateMaintenanceDeliveryWorker(
                (_, _) => Task.CompletedTask, TimeProvider.System, _ => { }));
            await executor.ExecuteAsync(f.Packet, cancellation.Token);
            f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
            await executor.EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(2, delivered);
        }
        finally { cancellation.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }

    private static async Task QueueAsync(Fixture f)
    {
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default);
    }
}
