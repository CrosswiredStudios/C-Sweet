using System.IO.Pipes;
using CSweet.Compute.Contracts;
using CSweet.Compute.Guest;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeGuestConnectionTests
{
    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public async Task Provider_command_roundtrip_preserves_identity_output_and_exit_code(string os)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var process = new Process(); var creates = 0;
        var execution = new ComputeGuestExecution(os, _ => { creates++; return process; });
        var command = new ComputeGuestCommand(Guid.NewGuid(), os == "linux" ? "/usr/bin/test" : "C:\\Tools\\test.exe", os == "linux" ? "/work" : "C:\\work", [], 2, 4096);
        var result = await ExchangeAsync(stream => new ComputeGuestConnection(execution).ServeAsync(stream, timeout.Token),
            connect => ComputeGuestCommandClient.ExecuteAsync(connect, command, os, timeout.Token), timeout.Token);
        Assert.Equal(command.RequestId, result.RequestId); Assert.Equal(17, result.ExitCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.StandardOutput); Assert.Equal(1, creates); Assert.Equal(1, process.Stops);
    }

    [Fact]
    public async Task Existing_readiness_probe_works_without_starting_a_command()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = new ComputeGuestExecution("windows", _ => throw new Exception("Readiness must not execute"));
        var result = await ExchangeAsync(stream => new ComputeGuestConnection(execution).ServeAsync(stream, timeout.Token),
            connect => ComputeGuestReadiness.ProbeAsync(connect, OperatingSystem.IsWindows() ? "windows" : "linux",
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), timeout.Token), timeout.Token);
        Assert.True(result);
    }

    [Fact]
    public async Task Wrong_command_identity_is_rejected_without_reconnecting()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = new ComputeGuestCommand(Guid.NewGuid(), "C:\\test.exe", "C:\\work", [], 2, 4096);
        var accepts = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeAsync(async stream =>
        {
            accepts++;
            var request = await ComputeGuestWire.ReadAsync<ComputeGuestWireRequest>(stream, ComputeGuestWire.MaximumRequestBytes, timeout.Token);
            await ComputeGuestWire.WriteAsync(stream, new ComputeGuestWireResult(1, request.Challenge, Guid.NewGuid(), 0, false, [], [], false),
                ComputeGuestWire.MaximumResultBytes, timeout.Token);
        }, connect => ComputeGuestCommandClient.ExecuteAsync(connect, command, "windows", timeout.Token), timeout.Token));
        Assert.Equal(1, accepts);
    }

    private static async Task<T> ExchangeAsync<T>(Func<Stream, Task> serve,
        Func<Func<CancellationToken, Task<Stream>>, Task<T>> invoke, CancellationToken token)
    {
        var name = "csweet-guest-mvp-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var handler = Task.Run(async () => { await server.WaitForConnectionAsync(token); await serve(server); }, token);
        try
        {
            return await invoke(async cancellation =>
            {
                var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await client.ConnectAsync(cancellation); return client; }
                catch { client.Dispose(); throw; }
            });
        }
        finally { await handler; }
    }

    private sealed class Process : IComputeGuestProcess
    {
        public Stream StandardOutput { get; } = new MemoryStream([1, 2, 3]);
        public Stream StandardError { get; } = new MemoryStream();
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<int> WaitForExitAsync(CancellationToken token) => Task.FromResult(17);
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
    }
}
