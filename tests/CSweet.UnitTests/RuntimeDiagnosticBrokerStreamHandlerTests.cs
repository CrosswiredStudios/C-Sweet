using System.Text;
using CSweet.AgentBroker;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class RuntimeDiagnosticBrokerStreamHandlerTests
{
    [Fact]
    public async Task AcceptsOrderedBoundedRuntimeDiagnosticsAndRemovesUnsafeControls()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var handler = new RuntimeDiagnosticBrokerStreamHandler(workloadId, installationId);

        await handler.HandleAsync(new GuestBrokerStreamContext(
            workloadId, installationId, "runtime.logs", 0,
            Encoding.UTF8.GetBytes("started\nsecret\0tail"), false, null), default);

        Assert.Equal("started\nsecrettail", handler.Latest);
    }

    [Fact]
    public async Task RejectsUnexpectedOrReplayDiagnosticChunks()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var handler = new RuntimeDiagnosticBrokerStreamHandler(workloadId, installationId);

        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new GuestBrokerStreamContext(workloadId, installationId, "runtime.logs", 1,
                Encoding.UTF8.GetBytes("out of order"), false, null), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new GuestBrokerStreamContext(workloadId, installationId, "another.stream", 0,
                Encoding.UTF8.GetBytes("wrong stream"), false, null), default));
    }

    [Fact]
    public async Task RejectsOversizedAndInvalidUtf8Diagnostics()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var handler = new RuntimeDiagnosticBrokerStreamHandler(workloadId, installationId);

        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new GuestBrokerStreamContext(workloadId, installationId, "runtime.logs", 0,
                new byte[16 * 1024 + 1], false, null), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new GuestBrokerStreamContext(workloadId, installationId, "runtime.logs", 0,
                new byte[] { 0xff, 0xfe }, false, null), default));
    }
}
