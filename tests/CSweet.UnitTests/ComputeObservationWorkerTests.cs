using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeObservationWorkerTests
{
    [Fact]
    public async Task Startup_recovery_retries_unavailable_core_and_records_fresh_physical_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor();
        await executor.ExecuteAsync(f.Packet, default);
        var original = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        f.Journal.Core.Time.Now = original.Result.ExpiresAt;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0; var failures = new List<int>();
        var worker = new ComputeObservationRecoveryWorker(new(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time), (id, token) =>
        {
            if (++calls == 1) throw new IOException("Core unavailable.");
            return f.Journal.Core.Authorizer().ClaimAsync(id, token);
        }, async (packet, token) => { await executor.ExecuteAsync(packet, token); }, f.Journal.Core.Time, pass =>
        {
            failures.Add(pass.ConsecutiveFailures);
            if (pass.Recovery?.Observed == 1) stop.Cancel();
        }, initialRetry: TimeSpan.FromMilliseconds(100), maximumRetry: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(stop.Token));
        Assert.Equal(new[] { 1, 0 }, failures); Assert.Equal(2, calls);
        Assert.Equal(2, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)).Result.Sequence);
        Assert.Empty(f.Runner.Actions.Skip(1)); Assert.Equal(1, f.PayloadOpens);
    }

    [Fact]
    public async Task Cancellation_during_claim_preserves_expired_evidence_and_rejects_duplicate_worker_run()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Executor().ExecuteAsync(f.Packet, default);
        var original = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = original.Result.ExpiresAt;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ComputeObservationRecoveryWorker(new(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time), async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; },
            (_, _) => throw new InvalidOperationException("No claim."), f.Journal.Core.Time, _ => { });
        var run = worker.RunAsync(stop.Token);
        await entered.Task.WaitAsync(stop.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(stop.Token));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(original, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)));
    }

    [Fact]
    public async Task Authority_failure_escapes_without_retrying_or_changing_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Executor().ExecuteAsync(f.Packet, default);
        var original = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = original.Result.ExpiresAt;
        var calls = 0;
        var worker = new ComputeObservationRecoveryWorker(new(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time), (_, _) =>
        { calls++; throw new UnauthorizedAccessException("Invalid authority."); },
            (_, _) => Task.CompletedTask, f.Journal.Core.Time, _ => throw new InvalidOperationException("No retry report."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => worker.RunAsync(default));
        Assert.Equal(1, calls); Assert.Equal(original, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)));
    }
}
