using CSweet.Compute.Contracts;
using System.Threading.Channels;

namespace CSweet.Compute.Runtime;

public sealed record ComputeResultWorkerPass(ComputeResultDeliveryPass? Delivery, int ConsecutiveFailures, TimeSpan NextDelay);

/// <summary>Run independently of lease enforcement. The service host must supervise journal and configuration failures.</summary>
public sealed class ComputeResultDeliveryWorker
{
    private readonly ComputeResultOutboxDispatcher dispatcher;
    private readonly Func<ComputeResultOutboxEntry, CancellationToken, Task<ComputeResultAcknowledgement>> deliver;
    private readonly TimeProvider clock;
    private readonly Action<ComputeResultWorkerPass> report;
    private readonly TimeSpan recoveryInterval, initialRetry, maximumRetry;
    private readonly Channel<byte> wakes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private int running;

    public ComputeResultDeliveryWorker(ComputeReplayJournal journal,
        Func<ComputeResultOutboxEntry, CancellationToken, Task<ComputeResultAcknowledgement>> deliver, TimeProvider clock,
        Action<ComputeResultWorkerPass> report, TimeSpan? recoveryInterval = null,
        TimeSpan? initialRetry = null, TimeSpan? maximumRetry = null)
    {
        dispatcher = new(journal, clock); this.deliver = deliver; this.clock = clock; this.report = report;
        this.recoveryInterval = recoveryInterval ?? TimeSpan.FromSeconds(30);
        this.initialRetry = initialRetry ?? TimeSpan.FromSeconds(1);
        this.maximumRetry = maximumRetry ?? TimeSpan.FromSeconds(60);
        if (this.recoveryInterval < TimeSpan.FromMilliseconds(100) || this.recoveryInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(recoveryInterval));
        if (this.initialRetry < TimeSpan.FromMilliseconds(100) || this.initialRetry > this.maximumRetry)
            throw new ArgumentOutOfRangeException(nameof(initialRetry));
        if (this.maximumRetry > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(maximumRetry));
    }

    public void NotifyStateChanged() => wakes.Writer.TryWrite(0);

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("This result delivery worker is already running.");
        try
        {
            var failures = 0;
            Guid? cursor = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while (wakes.Reader.TryRead(out _)) { }
                ComputeResultDeliveryPass? pass = null;
                var failed = false;
                try
                {
                    pass = await dispatcher.DispatchAsync(cursor, async (row, cancellation) =>
                    {
                        try { return await deliver(row, cancellation); }
                        catch (Exception error) when (!cancellation.IsCancellationRequested &&
                            error is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
                        { throw new DeliveryUnavailableException(); }

                    }, token);
                    cursor = pass.NextAfterOperationId;
                    failures = 0;
                }
                catch (DeliveryUnavailableException) { failed = true; failures = Math.Min(failures + 1, 31); }
                // Only transport failures are retried here. Journal reads/acknowledgements and signing
                // configuration faults escape to supervision, with evidence still durable.
                var delay = failed
                    ? TimeSpan.FromTicks((long)Math.Min(maximumRetry.Ticks, initialRetry.Ticks * Math.Pow(2, failures - 1)))
                    : cursor is not null ? initialRetry : recoveryInterval;
                report(new(pass, failures, delay));
                token.ThrowIfCancellationRequested();
                if (failed || cursor is not null)
                {
                    // Hints cannot turn outages or a full backlog into an unbounded request loop.
                    await Task.Delay(delay, clock, token);
                    continue;
                }
                using var deadline = new CancellationTokenSource(delay, clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                try { await wakes.Reader.ReadAsync(linked.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private sealed class DeliveryUnavailableException : Exception;
}
