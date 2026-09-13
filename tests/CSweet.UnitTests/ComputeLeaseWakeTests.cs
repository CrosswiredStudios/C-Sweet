using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeLeaseWakeTests
{
    [Fact]
    public async Task Executor_wakes_its_resident_monitor_after_recording_a_new_reservation()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var executor = f.Executor();
        var initialPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ComputeLeaseMonitorPass? observed = null;
        var monitor = executor.CreateLeaseMonitor(f.Journal.Core.Time, pass =>
        {
            initialPass.TrySetResult();
            if (pass.Examined > 0) { observed = pass; cancellation.Cancel(); }
        });
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            await initialPass.Task.WaitAsync(cancellation.Token);
            Assert.Null(observed);
            await executor.ExecuteAsync(f.Packet, default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.NotNull(observed); Assert.Equal(1, observed.Examined); Assert.Equal(0, observed.Attempted);
            Assert.Empty(f.Runner.Actions.Skip(1));
        }
        finally { cancellation.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }
}
