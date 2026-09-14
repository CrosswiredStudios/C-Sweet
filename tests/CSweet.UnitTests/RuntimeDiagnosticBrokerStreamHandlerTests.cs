using System.Text;
using CSweet.AgentBroker;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class RuntimeDiagnosticBrokerStreamHandlerTests
{
    [Fact]
    public async Task PreservesFailureHeaderAfterIdleLoggingAndGuestExitReplaceTheRollingTail()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var handler = new RuntimeDiagnosticBrokerStreamHandler(workloadId, installationId);
        await handler.HandleAsync(new(workloadId, installationId, "runtime.logs", 0,
            Encoding.UTF8.GetBytes("info: previous request\nfail: AgentRuntimeWorker\nHttpRequestException: request rejected (413)\n" +
                new string('s', 6000) + "\ninfo: renewing lease"), false, null), default);
        await handler.HandleAsync(new(workloadId, installationId, "runtime.logs", 1,
            Encoding.UTF8.GetBytes("info: " + new string('x', 10000)), false, null), default);
        handler.CaptureExitDetail("info: idle timeout elapsed\0");

        Assert.Contains("HttpRequestException: request rejected (413)", handler.Latest);
        Assert.EndsWith("info: idle timeout elapsed", handler.Latest);
        Assert.DoesNotContain('\0', handler.Latest!);
        Assert.InRange(handler.Latest!.Length, 1, 8192);
    }

    [Fact]
    public void CapturesBoundedExitDetailWhenBuilderNeverStreamsDiagnostics()
    {
        var handler = new RuntimeDiagnosticBrokerStreamHandler(Guid.NewGuid(), Guid.NewGuid());
        handler.CaptureExitDetail(new string('x', 9000) + "\0\npermission denied");
        Assert.Equal(8192, handler.Latest!.Length);
        Assert.EndsWith("\npermission denied", handler.Latest);
        Assert.DoesNotContain('\0', handler.Latest);
        handler.CaptureExitDetail(null);
        Assert.EndsWith("\npermission denied", handler.Latest);
    }

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
