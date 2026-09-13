using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeReceiptRecoveryWorkerTests
{
    [Theory]
    [InlineData(ComputeResultDisposition.Recorded)]
    [InlineData(ComputeResultDisposition.Superseded)]
    [InlineData(ComputeResultDisposition.Completed)]
    public async Task Receipt_recovery_retries_transport_and_removes_exact_evidence_without_claim_or_observation(ComputeResultDisposition disposition)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Executor().ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = row.Result.ExpiresAt;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var calls = 0;
        var worker = new ComputeObservationRecoveryWorker(new(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time),
            (_, _) => throw new InvalidOperationException("Receipt recovery must not claim execution."),
            (_, _) => throw new InvalidOperationException("Receipt recovery must not observe."), f.Journal.Core.Time,
            pass => { if (pass.Recovery?.Acknowledged == 1) stop.Cancel(); },
            initialRetry: TimeSpan.FromMilliseconds(100), maximumRetry: TimeSpan.FromMilliseconds(100),
            readReceipt: (pending, _) =>
            {
                Assert.Equal(row, pending);
                if (++calls == 1) throw new IOException("Core unavailable.");
                return Task.FromResult<ComputeResultAcknowledgement?>(new(pending.Result.OperationId, pending.Result.Sequence, pending.Digest, false, disposition));
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(stop.Token));
        Assert.Equal(2, calls); Assert.Empty(await f.Journal.Journal().ListResultsAsync(100, default));
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)); Assert.Empty(f.Runner.Actions.Skip(1));
    }

    [Fact]
    public async Task Mismatched_receipt_stops_recovery_without_removing_evidence_or_claiming()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Executor().ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = row.Result.ExpiresAt;
        await Assert.ThrowsAsync<InvalidDataException>(() => new ComputeObservationRecovery(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time)
            .RecoverAsync(null, (_, _) => throw new InvalidOperationException("No claim permitted."), (_, _) => Task.CompletedTask, default,
                (_, _) => Task.FromResult<ComputeResultAcknowledgement?>(new(row.Result.OperationId, row.Result.Sequence, "wrong", false))));
        Assert.Equal(row, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)));
    }
}
