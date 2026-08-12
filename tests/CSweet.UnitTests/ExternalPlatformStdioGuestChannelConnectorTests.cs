using System.Text;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.UnitTests;

public sealed class ExternalPlatformStdioGuestChannelConnectorTests
{
    [Fact]
    public async Task HandshakeReaderStopsAtNewlineWithoutConsumingBrokerBytes()
    {
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes("{\"success\":true,\"guestChannelTransport\":\"stdio-duplex-v1\"}\nbroker-frame"));

        var response = await ExternalPlatformStdioGuestChannelConnector.ReadHandshakeAsync(
            stream, CancellationToken.None);
        var remaining = new byte[32];
        var read = await stream.ReadAsync(remaining);

        Assert.True(response.Success);
        Assert.Equal(ExternalPlatformStdioGuestChannelConnector.TransportName,
            response.GuestChannelTransport);
        Assert.Equal("broker-frame", Encoding.UTF8.GetString(remaining, 0, read));
    }

    [Fact]
    public async Task HandshakeReaderRejectsOversizedOrAmbiguousFraming()
    {
        await using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 4098)));
        await using var carriageReturn = new MemoryStream(Encoding.UTF8.GetBytes("{}\r\n"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExternalPlatformStdioGuestChannelConnector.ReadHandshakeAsync(
                oversized, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExternalPlatformStdioGuestChannelConnector.ReadHandshakeAsync(
                carriageReturn, CancellationToken.None));
    }

    [Fact]
    public void ConnectorRejectsWrongProviderAndControlCharacters()
    {
        var workloadId = Guid.NewGuid();

        Assert.Throws<InvalidDataException>(() =>
            ExternalPlatformStdioGuestChannelConnector.ValidateHandle(
                new IsolationWorkloadHandle("wrong", workloadId, "instance", IsolationWorkloadKind.Runtime),
                "firecracker-kvm"));
        Assert.Throws<InvalidDataException>(() =>
            ExternalPlatformStdioGuestChannelConnector.ValidateHandle(
                new IsolationWorkloadHandle("firecracker-kvm", workloadId, "bad\ninstance", IsolationWorkloadKind.Runtime),
                "firecracker-kvm"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stdio-duplex-v2")]
    public void HandshakeMustConfirmCertifiedTransport(string? transport)
    {
        Assert.Throws<IsolationUnavailableException>(() =>
            ExternalPlatformStdioGuestChannelConnector.ValidateHandshake(
                new PlatformHelperResponse
                {
                    Success = true,
                    GuestChannelTransport = transport
                }));
    }
}
