using CSweet.UI.Setup;

namespace CSweet.UnitTests;

public sealed class SetupStatusPollerTests
{
    [Fact]
    public async Task PollsSequentiallyAndNeverOverlapsRequests()
    {
        using var cancellation = new CancellationTokenSource();
        var active = 0;
        var maximumActive = 0;
        var polls = 0;
        var delays = 0;

        await SetupStatusPoller.RunAsync(async _ =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            await Task.Yield();
            Interlocked.Decrement(ref active);
            if (++polls == 5) cancellation.Cancel();
            return false;
        }, new SetupPollSchedule(), cancellation.Token, (_, _) => { delays++; return Task.CompletedTask; });

        Assert.Equal(5, polls);
        Assert.Equal(1, maximumActive);
        Assert.Equal(5, delays); // exactly one wait before every request: no bursts after a slow tick
    }

    [Fact]
    public async Task CancellationStopsTheLoopBeforeAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var polls = 0;
        cancellation.Cancel();

        await SetupStatusPoller.RunAsync(_ => { polls++; return Task.FromResult(false); },
            new SetupPollSchedule(), cancellation.Token, (_, token) => Task.Delay(Timeout.Infinite, token));

        Assert.Equal(0, polls);
    }

    [Fact]
    public async Task FailuresBackOffAndASuccessResetsTheDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var call = 0;

        await SetupStatusPoller.RunAsync(_ =>
        {
            call++;
            if (call >= 6) cancellation.Cancel();
            return call <= 4 ? throw new HttpRequestException("offline") : Task.FromResult(true);
        }, new SetupPollSchedule(), cancellation.Token, (delay, _) => { waits.Add(delay); return Task.CompletedTask; });

        Assert.Equal(SetupPollSchedule.Steady, waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), waits[1]);
        Assert.Equal(TimeSpan.FromSeconds(20), waits[2]);
        Assert.Equal(SetupPollSchedule.Maximum, waits[3]);
        Assert.Equal(SetupPollSchedule.Maximum, waits[4]);
        Assert.Equal(SetupPollSchedule.Changing, waits[5]);
    }

    [Fact]
    public void UnchangedStateSlowsToTheIdleInterval()
    {
        var schedule = new SetupPollSchedule();
        var delays = Enumerable.Range(0, 13).Select(_ => schedule.Next(succeeded: true, changed: false)).ToArray();

        Assert.All(delays[..11], delay => Assert.Equal(SetupPollSchedule.Steady, delay));
        Assert.Equal(SetupPollSchedule.Idle, delays[^1]);
        Assert.Equal(SetupPollSchedule.Changing, schedule.Next(succeeded: true, changed: true));
    }
}
