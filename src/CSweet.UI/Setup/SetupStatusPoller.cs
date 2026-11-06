namespace CSweet.UI.Setup;

/// <summary>
/// Delay policy for setup status polling: quick while something is changing, steady while waiting,
/// slower when nothing changes for a while, and exponential backoff after failures.
/// </summary>
public sealed class SetupPollSchedule
{
    public static readonly TimeSpan Changing = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Steady = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(30);
    private const int IdleAfterUnchangedPolls = 12;

    private int _failures;
    private int _unchanged;

    public TimeSpan Initial => Steady;

    public TimeSpan Next(bool succeeded, bool changed)
    {
        if (!succeeded)
        {
            _unchanged = 0;
            _failures = Math.Min(_failures + 1, 8);
            var backoff = TimeSpan.FromTicks(Steady.Ticks * (1L << _failures));
            return backoff < Maximum ? backoff : Maximum;
        }
        _failures = 0;
        if (changed)
        {
            _unchanged = 0;
            return Changing;
        }
        _unchanged++;
        return _unchanged < IdleAfterUnchangedPolls ? Steady : Idle;
    }
}

/// <summary>
/// One sequential polling loop. The next request is scheduled only after the previous one finished,
/// so requests never overlap, missed timer ticks never queue up into a burst, and cancellation
/// (component disposal) stops the loop before another request is sent.
/// </summary>
public static class SetupStatusPoller
{
    public static async Task RunAsync(
        Func<CancellationToken, Task<bool>> pollOnce,
        SetupPollSchedule schedule,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(pollOnce);
        ArgumentNullException.ThrowIfNull(schedule);
        delay ??= Task.Delay;
        var next = schedule.Initial;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(next, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            bool changed;
            try { changed = await pollOnce(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch
            {
                // A transient refresh failure must not end the waiting experience; back off instead.
                next = schedule.Next(succeeded: false, changed: false);
                continue;
            }
            next = schedule.Next(succeeded: true, changed);
        }
    }
}
