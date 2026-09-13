using CSweet.Compute.Contracts;
using CSweet.Compute.Guest;

namespace CSweet.UnitTests;

public sealed class ComputeGuestExecutionFailureTests
{
    private static ComputeGuestCommand Command() => new(Guid.NewGuid(), "/bin/test", "/work", [], 1, 64);

    [Fact]
    public async Task Invalid_command_and_pre_cancelled_call_do_not_create_a_process()
    {
        var creates = 0;
        var runner = new ComputeGuestExecution("linux", _ => { creates++; throw new Exception("Unexpected launch"); });
        await Assert.ThrowsAsync<ArgumentException>(() => runner.ExecuteAsync(Command() with { TimeoutSeconds = 0 }, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(Command(), cancelled.Token));
        Assert.Equal(0, creates); Assert.True(runner.AcceptingWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_read_or_disposal_failure_never_reports_success_and_disables_readiness(bool disposalFailure)
    {
        var process = new FaultingProcess(disposalFailure);
        var runner = new ComputeGuestExecution("linux", _ => process);
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(Command(), default));
        Assert.Equal(1, process.Stops); Assert.False(runner.AcceptingWork);
    }

    private sealed class FaultingProcess(bool disposalFailure) : IComputeGuestProcess
    {
        public Stream StandardOutput { get; } = new FaultingStream(disposalFailure);
        public Stream StandardError { get; } = new MemoryStream();
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<int> WaitForExitAsync(CancellationToken token) => Task.FromResult(0);
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
    }

    private sealed class FaultingStream(bool disposalFailure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            disposalFailure ? ValueTask.FromResult(0) : ValueTask.FromException<int>(new IOException("Read failed"));
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposalFailure) throw new IOException("Dispose failed");
        }
    }
}
