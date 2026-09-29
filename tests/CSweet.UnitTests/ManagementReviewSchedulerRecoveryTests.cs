using System.Threading.Channels;
using CSweet.AgentHost.Broker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class ManagementReviewSchedulerRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPass_DoesNotStopHost_AndRetriesOnNextTick(bool independentCancellation)
    {
        var clock = new ManualTimerClock();
        var scopes = new UnavailableScopes(independentCancellation);
        var logger = new FailureLogger();
        using var scheduler = new ManagementReviewScheduler(scopes, clock, logger);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var first = await logger.Failures.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType(independentCancellation ? typeof(OperationCanceledException) : typeof(TimeoutException), first);
            Assert.False(scheduler.ExecuteTask!.IsCompleted);
            Assert.Equal(1, scopes.Attempts);
            Assert.Equal(TimeSpan.FromMinutes(1), clock.Period);

            clock.Tick();
            await logger.Failures.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, scopes.Attempts);
            Assert.False(scheduler.ExecuteTask.IsCompleted);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(scheduler.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(clock.Disposed);
        Assert.False(logger.Failures.Reader.TryRead(out _));
    }

    private sealed class UnavailableScopes(bool independentCancellation) : IServiceScopeFactory
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref attempts);
            if (independentCancellation) throw new OperationCanceledException("A dependency cancelled its request.");
            throw new TimeoutException("The database connection is temporarily unavailable.");
        }
    }

    private sealed class FailureLogger : ILogger<ManagementReviewScheduler>
    {
        public Channel<Exception> Failures { get; } = Channel.CreateUnbounded<Exception>();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null) Failures.Writer.TryWrite(exception);
        }
    }

    private sealed class ManualTimerClock : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        public TimeSpan Period { get; private set; }
        public bool Disposed { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            Period = period;
            return new ManualTimer(this);
        }
        public void Tick() => callback!(state);
        private sealed class ManualTimer(ManualTimerClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => !clock.Disposed;
            public void Dispose() => clock.Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
