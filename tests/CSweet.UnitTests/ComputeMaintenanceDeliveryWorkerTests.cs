using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceDeliveryWorkerTests
{
    [Fact]
    public async Task Startup_recovers_evidence_and_retries_identical_rows_with_capped_backoff_despite_wakes()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await QueueAsync(f);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var attempts = new List<ComputeMaintenanceOutboxEntry>();
        var passes = new List<ComputeMaintenanceDeliveryPass>();
        var timestamps = new List<long>();
        ComputeMaintenanceDeliveryWorker? worker = null;
        worker = new(f.Journal.Journal(), (row, _) =>
        {
            attempts.Add(row); timestamps.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            if (attempts.Count <= 3) throw new IOException("Connection lost after remote commit.");
            return Task.CompletedTask;
        }, TimeProvider.System, pass =>
        {
            passes.Add(pass);
            if (pass.ConsecutiveFailures == 0) cancellation.Cancel();
            else for (var i = 0; i < 1000; i++) worker!.NotifyStateChanged();
        }, initialRetry: TimeSpan.FromMilliseconds(100), maximumRetry: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cancellation.Token));
        Assert.Equal(new[] { 1, 2, 3, 0 }, passes.Select(x => x.ConsecutiveFailures));
        Assert.Equal(new[] { 100d, 200d, 200d }, passes.Take(3).Select(x => x.NextDelay.TotalMilliseconds));
        Assert.Equal(5, attempts.Count);
        Assert.All(attempts.Take(4), row => Assert.Equal(attempts[0], row));
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(timestamps[0], timestamps[3]) >= TimeSpan.FromMilliseconds(450));
        Assert.Empty(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_queue_recovers_new_evidence_from_a_hint_or_timer(bool hint)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        var worker = new ComputeMaintenanceDeliveryWorker(f.Journal.Journal(), async (_, token) =>
        {
            // A blocked or slow network connection must never own the physical journal lock.
            Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, token));
            delivered++;
        }, TimeProvider.System, pass =>
        {
            initial.TrySetResult();
            if (pass.Delivered == 2) cancellation.Cancel();
        }, recoveryInterval: hint ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        var running = worker.RunAsync(cancellation.Token);
        try
        {
            await initial.Task.WaitAsync(cancellation.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(cancellation.Token));
            await QueueAsync(f);
            if (hint) worker.NotifyStateChanged();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(2, delivered);
            Assert.Empty(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        }
        finally { cancellation.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task Shutdown_interrupts_delivery_without_acknowledging_and_restart_recovers()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await QueueAsync(f);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ComputeMaintenanceDeliveryWorker(f.Journal.Journal(), async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, TimeProvider.System, _ => { });
        var running = worker.RunAsync(cancellation.Token);
        await entered.Task.WaitAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(2, (await f.Journal.Journal().ListMaintenanceEventsAsync(100, default)).Count);
        using var restarted = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var recovery = new ComputeMaintenanceDeliveryWorker(f.Journal.Journal(), (_, _) => Task.CompletedTask,
            TimeProvider.System, _ => restarted.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RunAsync(restarted.Token));
        Assert.Empty(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Journal_failure_escapes_to_supervision_without_network_retry(bool malformed)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(f.Journal.Root, "journal.json"), malformed ? "corrupt" : "{}");
        var attempts = 0;
        var worker = new ComputeMaintenanceDeliveryWorker(f.Journal.Journal(), (_, _) =>
        { attempts++; return Task.CompletedTask; }, TimeProvider.System, _ => Assert.Fail("Must not report a transport failure."));
        if (malformed) await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => worker.RunAsync(default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => worker.RunAsync(default));
        Assert.Equal(0, attempts);
    }

    private static async Task QueueAsync(Fixture f)
    {
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default);
    }
}
