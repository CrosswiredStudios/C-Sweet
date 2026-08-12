using System.Text;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Firecracker.Helper;

namespace CSweet.UnitTests;

public sealed class FirecrackerHelperSecurityTests
{
    [Fact]
    public void ArgumentsAllowOnlyTheFixedTypedProtocolSurface()
    {
        var parsed = HelperArguments.Parse(
            ["--protocol", "1.0", "--operation", "open-guest-channel"]);

        Assert.Equal("open-guest-channel", parsed.Operation);
        Assert.Throws<HelperProtocolException>(() => HelperArguments.Parse(
            ["--protocol", "1.0", "--operation", "shell"]));
        Assert.Throws<HelperProtocolException>(() => HelperArguments.Parse(
            ["--protocol", "1.0", "--operation", "probe", "--command", "id"]));
    }

    [Fact]
    public void JailerArgumentsEnforceNamespacesAndHardResourceLimitsWithoutNetworking()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-firecracker-test"));
        var paths = new FirecrackerHelperPaths(
            root, Path.Combine(root, "instances"), Path.Combine(root, "jailer"),
            Path.Combine(root, "artifacts"), Path.Combine(root, "firecracker"),
            Path.Combine(root, "jailer-bin"), Path.Combine(root, "vmlinux"), Path.Combine(root, "initrd.img"), 1001, 1001, 5000,
            "system.slice/csweet-runtime-host.service");
        var limits = new IsolationResourceLimits(2, 150, 512, 1024, 64, 1024, TimeSpan.FromMinutes(5));

        var arguments = FirecrackerHelperController.BuildJailerArguments(paths, "csweet-test", limits);
        var joined = string.Join(" ", arguments);

        Assert.Contains("--new-pid-ns", arguments);
        Assert.Contains("--daemonize", arguments);
        Assert.Contains("system.slice/csweet-runtime-host.service", arguments);
        Assert.Contains("memory.max=671088640", arguments);
        Assert.Contains("pids.max=64", arguments);
        Assert.Contains("cpu.max=150000 100000", arguments);
        Assert.DoesNotContain("--netns", arguments);
        Assert.DoesNotContain("network", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedPathsRejectTraversal()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-firecracker-test"));

        Assert.Throws<HelperProtocolException>(() =>
            FirecrackerHelperPaths.SafeChild(root, Path.Combine("..", "outside")));
        Assert.Throws<HelperProtocolException>(() =>
            FirecrackerHelperPaths.SafeChild(root, "bad\npath"));
    }

    [Fact]
    public async Task VsockHandshakeIsBoundedAndPreservesBrokerBytes()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("OK 1073741824\nbroker-frame"));

        var acknowledgement = await FirecrackerHelperController.ReadAsciiLineAsync(
            stream, 128, CancellationToken.None);
        var remaining = new byte[32];
        var read = await stream.ReadAsync(remaining);

        Assert.Equal("OK 1073741824", acknowledgement);
        Assert.Equal("broker-frame", Encoding.ASCII.GetString(remaining, 0, read));
    }

    [Fact]
    public async Task VsockHandshakeRejectsAmbiguousOrOversizedFraming()
    {
        await using var carriageReturn = new MemoryStream(Encoding.ASCII.GetBytes("OK 1\r\n"));
        await using var oversized = new MemoryStream(Encoding.ASCII.GetBytes(new string('a', 130)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FirecrackerHelperController.ReadAsciiLineAsync(carriageReturn, 128, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FirecrackerHelperController.ReadAsciiLineAsync(oversized, 128, CancellationToken.None));
    }

    [Fact]
    public void ReaperOnlySelectsExpiredRuntimeInstances()
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new FirecrackerInstanceMetadata(
            Guid.NewGuid(), Guid.NewGuid(), IsolationWorkloadKind.Runtime, "csweet-test", "/jail", int.MaxValue,
            3, now.AddMinutes(-10), now.AddMinutes(-9), null, now.AddSeconds(-1));

        Assert.True(FirecrackerHelperController.ShouldReap(metadata, now));
        Assert.False(FirecrackerHelperController.ShouldReap(
            metadata with { Kind = IsolationWorkloadKind.Builder }, now));
        Assert.False(FirecrackerHelperController.ShouldReap(
            metadata with { LeaseExpiresAt = now.AddMinutes(1), ProcessId = Environment.ProcessId }, now));
    }

    [Theory]
    [InlineData("Firecracker v1.13.1", "1.13.1")]
    [InlineData("jailer 1.13.1 (release)", "1.13.1")]
    [InlineData("not-a-version", null)]
    public void ToolVersionParsingIsStrict(string output, string? expected) =>
        Assert.Equal(expected, FirecrackerHelperController.ExtractVersion(output));
}
