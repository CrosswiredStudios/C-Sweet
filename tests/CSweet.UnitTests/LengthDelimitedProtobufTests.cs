using CSweet.AgentRuntime.Protocol;
using Google.Protobuf;

namespace CSweet.UnitTests;

public sealed class LengthDelimitedProtobufTests
{
    [Fact]
    public async Task RoundTrip_PreservesEnvelope()
    {
        var expected = new RuntimeHostEnvelope
        {
            ProtocolVersion = "1.0",
            RequestId = Guid.NewGuid().ToString("N"),
            ProbeRequest = new ProbeRequest { ProviderId = "hyperv" }
        };
        using var stream = new MemoryStream();

        await LengthDelimitedProtobuf.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await LengthDelimitedProtobuf.ReadAsync(stream, RuntimeHostEnvelope.Parser);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedLengthBeforeAllocation()
    {
        using var stream = new MemoryStream([0, 0, 16, 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LengthDelimitedProtobuf.ReadAsync(stream, RuntimeHostEnvelope.Parser, maximumFrameBytes: 4096));
    }

    [Fact]
    public async Task WriteAsync_RejectsOversizedMessage()
    {
        var envelope = new GuestEnvelope
        {
            ProtocolVersion = "1.0",
            MessageId = "large",
            StreamChunk = new StreamChunk { Content = ByteString.CopyFrom(new byte[4096]) }
        };
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LengthDelimitedProtobuf.WriteAsync(stream, envelope, maximumFrameBytes: 1024));
    }
}
