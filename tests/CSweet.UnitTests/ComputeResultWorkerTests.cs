using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeResultWorkerTests
{
    [Fact]
    public async Task Startup_retries_lost_response_then_clears_exact_pending_result()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        var original = Assert.Single(await f.Journal().ListResultsAsync(100, default));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var worker = new ComputeResultDeliveryWorker(f.Journal(), (row, _) =>
        {
            Assert.Equal(original, row);
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("Lost response.");
            return Task.FromResult(Ack(row));
        }, f.Core.Time, pass => { if (pass.Delivery?.Acknowledged == 1) done.TrySetResult(); },
            initialRetry: TimeSpan.FromMilliseconds(100), maximumRetry: TimeSpan.FromMilliseconds(100));
        var run = worker.RunAsync(stop.Token);
        await done.Task.WaitAsync(stop.Token); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, calls); Assert.Empty(await f.Journal().ListResultsAsync(100, default));
    }

    [Fact]
    public async Task Shutdown_during_delivery_retains_evidence_for_restart_and_duplicate_run_is_rejected()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ComputeResultDeliveryWorker(f.Journal(), async (row, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ack(row); }, f.Core.Time, _ => { });
        var run = worker.RunAsync(stop.Token); await entered.Task.WaitAsync(stop.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(stop.Token));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(await f.Journal().ListResultsAsync(100, default));
        var recovered = await new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time).DispatchAsync(null,
            (row, _) => Task.FromResult(Ack(row)), default);
        Assert.Equal(1, recovered.Acknowledged);
    }

    [Fact]
    public async Task Journal_corruption_escapes_to_supervision_without_transport_retry()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "journal.json"), "{invalid");
        var worker = new ComputeResultDeliveryWorker(f.Journal(), (_, _) => throw new InvalidOperationException("Unexpected delivery."), f.Core.Time,
            _ => throw new InvalidOperationException("Corruption must not be reported as a retry."));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => worker.RunAsync(default));
    }

    private static Task<bool> Observe(Fixture f) => f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
        Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning))), default);
    private static ComputeResultAcknowledgement Ack(ComputeResultOutboxEntry row) => new(row.Result.OperationId, row.Result.Sequence, row.Digest, false);
}
