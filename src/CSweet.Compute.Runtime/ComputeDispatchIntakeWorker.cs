using System.Threading.Channels;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public sealed record ComputeDispatchWorkerPass(ComputeDispatchIntakePass? Intake, int ConsecutiveFailures, TimeSpan NextDelay);

/// <summary>Coalesced work hints with bounded startup/reconnect discovery; independent of lease enforcement.</summary>
public sealed class ComputeDispatchIntakeWorker(ComputeDispatchIntake intake,
    Func<Guid?, CancellationToken, Task<ComputeProviderWorkPage>> discover,
    Func<Guid, CancellationToken, Task<ComputeDispatchPacket?>> claim,
    Func<ComputeDispatchPacket, CancellationToken, Task> execute,
    TimeProvider clock, Action<ComputeDispatchWorkerPass> report)
{
    private readonly Channel<byte> hints = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private int started;
    public void NotifyWorkAvailable() => hints.Writer.TryWrite(0);

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            throw new InvalidOperationException("This dispatch intake worker has already started.");
        Guid? cursor = null; var failures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            while (hints.Reader.TryRead(out _)) { }
            ComputeDispatchIntakePass? pass = null;
            try
            {
                pass = await intake.RunOnceAsync(cursor,
                    (after, ct) => TransportAsync(() => discover(after, ct)),
                    (id, ct) => TransportAsync(() => claim(id, ct)), execute, token);
                cursor = pass.NextAfterOperationId; failures = 0;
            }
            catch (TransportUnavailableException) { failures = Math.Min(31, failures + 1); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { failures = Math.Min(31, failures + 1); }
            // Authority and executor/journal faults escape. Never repeatedly issue physical work
            // merely because its outcome could not be recorded.
            var delay = failures > 0 ? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, failures - 1)))
                : cursor.HasValue ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
            report(new(pass, failures, delay));
            token.ThrowIfCancellationRequested();
            if (failures > 0 || cursor.HasValue)
            {
                await Task.Delay(delay, clock, token); // Hints cannot bypass outage/backlog pacing.
                continue;
            }
            using var deadline = new CancellationTokenSource(delay, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            try { await hints.Reader.ReadAsync(linked.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }

        async Task<T> TransportAsync<T>(Func<Task<T>> call)
        {
            try { return await call(); }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            { throw new TransportUnavailableException(); }
        }
    }

    private sealed class TransportUnavailableException : Exception;
}
