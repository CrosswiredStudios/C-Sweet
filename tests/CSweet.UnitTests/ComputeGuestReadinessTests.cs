using System.Buffers.Binary;
using System.IO.Pipes;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeGuestReadinessTests
{
    [Theory]
    [InlineData("ready", true)]
    [InlineData("busy-starting", false)]
    [InlineData("wrong-challenge", false)]
    [InlineData("wrong-os", false)]
    [InlineData("wrong-architecture", false)]
    [InlineData("wrong-version", false)]
    public async Task Framed_exchange_requires_fresh_matching_guest_liveness(string scenario, bool expected)
    {
        var name = "csweet-guest-readiness-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        async Task Respond()
        {
            var request = await ComputeGuestReadiness.ReadAsync<ComputeGuestReadinessRequest>(server, timeout.Token);
            Assert.Equal(1, request.Version); Assert.NotEqual(Guid.Empty, request.Challenge);
            await ComputeGuestReadiness.WriteAsync(server, new ComputeGuestReadinessResponse(
                scenario == "wrong-version" ? 2 : 1, scenario == "wrong-challenge" ? Guid.NewGuid() : request.Challenge,
                scenario == "wrong-os" ? "windows" : "linux", scenario == "wrong-architecture" ? "arm64" : "x64",
                scenario != "busy-starting"), timeout.Token);
        }
        var responding = Respond();
        try { Assert.Equal(expected, await ComputeGuestReadiness.ProbeAsync(_ => Task.FromResult<Stream>(client), "linux", "x64", timeout.Token)); }
        finally { await responding; }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public async Task Oversized_or_negative_frame_is_rejected_before_payload_allocation(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ComputeGuestReadiness.ReadAsync<ComputeGuestReadinessResponse>(stream, default));
    }

    [Fact]
    public async Task Transport_failure_is_not_readiness_and_cancellation_is_not_hidden()
    {
        Assert.False(await ComputeGuestReadiness.ProbeAsync(_ => throw new IOException("Offline"), "linux", "x64", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputeGuestReadiness.ProbeAsync(token =>
        { token.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(Stream.Null); }, "linux", "x64", cancelled.Token));
    }
}
