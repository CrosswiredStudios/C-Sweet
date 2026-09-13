using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public sealed record ComputeNotificationPass(bool WokeIntake, int ConsecutiveFailures);

/// <summary>Waits for authenticated hints; hints only wake current-state discovery.</summary>
public sealed class ComputeProviderNotificationWorker(Func<CancellationToken, Task<ComputeProviderWakeHint?>> receive,
    Action wakeIntake, TimeProvider clock, Action<ComputeNotificationPass> report)
{
    private int started;
    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0) throw new InvalidOperationException("Notification worker already started.");
        var failures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ComputeProviderWakeHint? hint = null;
            try { hint = await receive(token); failures = 0; }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is IOException or HttpRequestException or TimeoutException or OperationCanceledException)
            { failures = Math.Min(31, failures + 1); }
            if (hint is not null)
            {
                if (hint.EventId == Guid.Empty) throw new InvalidDataException("Notification identity is invalid.");
                wakeIntake();
            }
            report(new(hint is not null, failures));
            // Pace even unexpectedly immediate empty responses. Discovery has its own bounded recovery.
            await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 1 : Math.Min(60, Math.Pow(2, failures - 1))), clock, token);
        }
    }
}
