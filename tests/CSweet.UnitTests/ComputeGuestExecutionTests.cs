using CSweet.Compute.Contracts;
using CSweet.Compute.Guest;

namespace CSweet.UnitTests;

public sealed class ComputeGuestExecutionTests
{
    private static ComputeGuestCommand Command() => new(Guid.NewGuid(), "/usr/bin/test", "/work", [], 1, 12);

    [Fact]
    public async Task Capture_is_byte_bounded_across_both_streams_and_excess_is_drained()
    {
        var process = new Process { StandardOutput = new MemoryStream(new byte[10000]), StandardError = new MemoryStream(new byte[10000]) };
        var runner = new ComputeGuestExecution("linux", _ => process);
        var command = Command(); var result = await runner.ExecuteAsync(command, default);
        Assert.Equal(command.RequestId, result.RequestId); Assert.Equal(7, result.ExitCode);
        Assert.Equal(12, result.StandardOutput.Length + result.StandardError.Length);
        Assert.True(result.Truncated); Assert.False(result.TimedOut);
        Assert.Equal(1, process.Stops); Assert.True(runner.AcceptingWork);
    }

    [Fact]
    public async Task Parent_exit_stops_descendants_before_waiting_for_their_output_handles()
    {
        var pipe = new HeldOutput();
        var process = new Process { StandardOutput = pipe, OnStop = () => pipe.Complete() };
        var runner = new ComputeGuestExecution("linux", _ => process);
        var result = await runner.ExecuteAsync(Command(), default);
        Assert.False(result.TimedOut); Assert.Equal(7, result.ExitCode); Assert.Equal(1, process.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_caller_cancellation_both_stop_the_process(bool callerCancellation)
    {
        var process = new Process { Wait = async token => { await Task.Delay(Timeout.Infinite, token); return 0; } };
        var runner = new ComputeGuestExecution("linux", _ => process);
        using var source = new CancellationTokenSource();
        var task = runner.ExecuteAsync(Command(), source.Token);
        if (callerCancellation)
        {
            source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else
        {
            var result = await task; Assert.True(result.TimedOut); Assert.Null(result.ExitCode);
        }
        Assert.Equal(1, process.Stops); Assert.True(runner.AcceptingWork);
    }

    [Fact]
    public async Task Start_failure_still_cleans_up_and_never_reports_an_exit_code()
    {
        var process = new Process { Start = _ => throw new IOException("start failed") };
        var runner = new ComputeGuestExecution("linux", _ => process);
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(Command(), default));
        Assert.Equal(1, process.Stops); Assert.True(runner.AcceptingWork);
    }

    [Fact]
    public async Task Unconfirmed_cleanup_poison_readiness_and_prevents_another_launch()
    {
        var process = new Process { OnStop = () => throw new IOException("cleanup failed") }; var creates = 0;
        var runner = new ComputeGuestExecution("linux", _ => { creates++; return process; });
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(Command(), default));
        Assert.False(runner.AcceptingWork);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync(Command(), default));
        Assert.Equal(1, creates); Assert.Equal(1, process.Stops);
    }

    [Fact]
    public async Task Concurrent_work_is_rejected_without_creating_another_process()
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { Wait = token => exited.Task.WaitAsync(token) }; var creates = 0;
        var runner = new ComputeGuestExecution("linux", _ => { creates++; return process; });
        var first = runner.ExecuteAsync(Command() with { TimeoutSeconds = 30 }, default);
        Assert.False(runner.AcceptingWork);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync(Command(), default));
        exited.SetResult(0); await first; Assert.Equal(1, creates); Assert.True(runner.AcceptingWork);
    }

    private sealed class Process : IComputeGuestProcess
    {
        public Stream StandardOutput { get; init; } = new MemoryStream();
        public Stream StandardError { get; init; } = new MemoryStream();
        public Func<CancellationToken, Task> Start { get; init; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<int>> Wait { get; init; } = _ => Task.FromResult(7);
        public Action OnStop { get; init; } = () => { };
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken token) => Start(token);
        public Task<int> WaitForExitAsync(CancellationToken token) => Wait(token);
        public Task StopAsync(CancellationToken token) { Stops++; OnStop(); return Task.CompletedTask; }
    }

    private sealed class HeldOutput : MemoryStream
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => closed.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await closed.Task.WaitAsync(token); return 0; }
    }
}
