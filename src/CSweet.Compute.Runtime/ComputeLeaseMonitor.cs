using System.Threading.Channels;

namespace CSweet.Compute.Runtime;

public sealed record ComputeLeaseMonitorPass(int Examined, int Attempted, int Failed, IReadOnlyList<Guid> FailedEnvironmentIds);

/// <summary>
/// Provider-resident expiry recovery. Wakes are hints; persisted leases are re-read before effects.
/// This does not require a Core connection or agent polling. The service host must supervise RunAsync.
/// </summary>
public sealed class ComputeLeaseMonitor
{
    private readonly ComputeReplayJournal journal;
    private readonly Func<Guid, CancellationToken, Task<bool>> enforce;
    private readonly TimeProvider clock;
    private readonly Action<ComputeLeaseMonitorPass> report;
    private readonly TimeSpan recoveryInterval;
    private readonly Channel<byte> wakes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private int running;

    public ComputeLeaseMonitor(ComputeReplayJournal journal, Func<Guid, CancellationToken, Task<bool>> enforce,
        TimeProvider clock, Action<ComputeLeaseMonitorPass> report, TimeSpan? recoveryInterval = null)
    {
        this.journal = journal; this.enforce = enforce; this.clock = clock; this.report = report;
        this.recoveryInterval = recoveryInterval ?? TimeSpan.FromSeconds(30);
        if (this.recoveryInterval < TimeSpan.FromMilliseconds(100) || this.recoveryInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(recoveryInterval));
    }

    public void NotifyStateChanged() => wakes.Writer.TryWrite(0);

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("This lease monitor is already running.");
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while (wakes.Reader.TryRead(out _)) { }
                DateTimeOffset? nextDue = null;
                var examined = 0; var attempted = 0; var failed = 0;
                var failures = new List<Guid>(); Guid? cursor = null;
                while (true)
                {
                    // Page reads fail closed on missing/corrupt history. Never initialize it here.
                    var page = await journal.ListReservationsAsync(cursor, 100, token);
                    foreach (var reservation in page)
                    {
                        token.ThrowIfCancellationRequested(); examined++;
                        if (!reservation.LeaseExpired && reservation.LeaseExpiresAt > clock.GetUtcNow())
                        {
                            if (nextDue is null || reservation.LeaseExpiresAt < nextDue) nextDue = reservation.LeaseExpiresAt;
                            continue;
                        }
                        attempted++;
                        try { await enforce(reservation.EnvironmentId, token); }
                        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException)
                        {
                            // One workload failure must not starve other due leases. The journal retained
                            // its fence/attempt; retry on a later recovery pass. No raw exception is exposed.
                            failed++;
                            if (failures.Count < 100) failures.Add(reservation.EnvironmentId);
                        }
                    }
                    if (page.Count < 100) break;
                    cursor = page[^1].EnvironmentId;
                }
                report(new(examined, attempted, failed, failures.ToArray()));
                token.ThrowIfCancellationRequested();
                var delay = nextDue is { } due ? due - clock.GetUtcNow() : recoveryInterval;
                if (delay > recoveryInterval) delay = recoveryInterval;
                // Known expired failures retry at the recovery interval, not in a tight loop after a slow pass.
                if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(100);
                using var deadline = new CancellationTokenSource(delay, clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                try { await wakes.Reader.ReadAsync(linked.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
