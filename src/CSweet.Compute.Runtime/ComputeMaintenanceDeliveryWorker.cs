using System.Threading.Channels;

namespace CSweet.Compute.Runtime;

public sealed record ComputeMaintenanceDeliveryPass(int Delivered, int ConsecutiveFailures, TimeSpan NextDelay);

/// <summary>Run independently of lease enforcement. The service host must supervise journal and configuration failures.</summary>
public sealed class ComputeMaintenanceDeliveryWorker
{
    private readonly ComputeMaintenanceOutboxDispatcher dispatcher;
    private readonly Func<ComputeMaintenanceOutboxEntry, CancellationToken, Task> deliver;
    private readonly TimeProvider clock;
    private readonly Action<ComputeMaintenanceDeliveryPass> report;
    private readonly TimeSpan recoveryInterval, initialRetry, maximumRetry;
    private readonly Channel<byte> wakes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private int running;

    public ComputeMaintenanceDeliveryWorker(ComputeReplayJournal journal,
        Func<ComputeMaintenanceOutboxEntry, CancellationToken, Task> deliver, TimeProvider clock,
        Action<ComputeMaintenanceDeliveryPass> report, TimeSpan? recoveryInterval = null,
        TimeSpan? initialRetry = null, TimeSpan? maximumRetry = null)
    {
        dispatcher = new(journal); this.deliver = deliver; this.clock = clock; this.report = report;
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
            throw new InvalidOperationException("This maintenance delivery worker is already running.");
        try
        {
            var failures = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while (wakes.Reader.TryRead(out _)) { }
                var delivered = 0;
                var failed = false;
                try
                {
                    await dispatcher.DispatchAsync(async (row, cancellation) =>
                    {
                        try { await deliver(row, cancellation); }
                        catch (Exception error) when (!cancellation.IsCancellationRequested &&
                            error is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
                        { throw new DeliveryUnavailableException(); }
                        delivered++;
                    }, token);
                    failures = 0;
                }
                catch (DeliveryUnavailableException) { failed = true; failures = Math.Min(failures + 1, 31); }
                // Only transport failures are retried here. Journal reads/acknowledgements and signing
                // configuration faults escape to supervision, with evidence still durable.
                var delay = failed
                    ? TimeSpan.FromTicks((long)Math.Min(maximumRetry.Ticks, initialRetry.Ticks * Math.Pow(2, failures - 1)))
                    : delivered == 100 ? initialRetry : recoveryInterval;
                report(new(delivered, failures, delay));
                token.ThrowIfCancellationRequested();
                if (failed || delivered == 100)
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
