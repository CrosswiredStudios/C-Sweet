using CSweet.AgentRuntime.HyperV;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class LocalExecutionNodeProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"csweet-local-provisioner-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("linux")]
    [InlineData("macos")]
    public void CompleteUnixPayloadResolves(string platform)
    {
        var payload = Path.Combine(_root, $"{platform}-runtime");
        foreach (var relative in RequiredFiles(platform))
        {
            var path = Path.Combine(payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test");
        }

        var resolved = LocalExecutionNodeProvisioner.TryResolveUnixInstaller(
            platform, _root, out var installer, out var packageRoot);

        Assert.True(resolved);
        Assert.Equal(Path.Combine(payload, "install-execution-node.sh"), installer);
        Assert.Equal(payload, packageRoot);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("macos")]
    public void IncompleteUnixPayloadDoesNotResolve(string platform)
    {
        var payload = Path.Combine(_root, $"{platform}-runtime");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "install-execution-node.sh"), "#!/bin/sh");

        Assert.False(LocalExecutionNodeProvisioner.TryResolveUnixInstaller(
            platform, _root, out _, out _));
    }

    [Fact]
    public void ShellAndAppleScriptEscapingKeepPathsInsideOneArgument()
    {
        var quoted = LocalExecutionNodeProvisioner.ShellQuote("/tmp/C-Sweet's payload");
        var escaped = LocalExecutionNodeProvisioner.EscapeAppleScript(quoted + " \"value\"");

        Assert.Equal("'/tmp/C-Sweet'\\''s payload'", quoted);
        Assert.Equal("'/tmp/C-Sweet'\\\\''s payload' \\\"value\\\"", escaped);
    }

    [Fact]
    public void ElevatedResultPathsAreFixedSystemLocations()
    {
        var jobId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        Assert.Equal(
            "/var/lib/csweet/setup/local-provisioning-00112233445566778899aabbccddeeff.result",
            LocalExecutionNodeProvisioner.UnixResultPath("linux", jobId));
        Assert.Equal(
            "/Library/Application Support/CSweet/Setup/local-provisioning-00112233445566778899aabbccddeeff.result",
            LocalExecutionNodeProvisioner.UnixResultPath("macos", jobId));
    }

    [Fact]
    public void WindowsProgressPreservesEstimatedRemainingRange()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var windows = new WindowsRuntimeHostProvisioningProgress(
            Guid.NewGuid(), "windows-isolation", WindowsRuntimeHostProvisioningState.Running,
            "build-guest", "Building the hardened guest image", "Preparing the guest image.",
            24, startedAt, startedAt.AddMinutes(1), 900, 2100,
            false, null, null, Environment.ProcessId);

        var progress = LocalExecutionNodeProvisioner.MapWindowsProgress(windows);

        Assert.NotNull(progress);
        Assert.Equal(900, progress.EstimatedRemainingMinimumSeconds);
        Assert.Equal(2100, progress.EstimatedRemainingMaximumSeconds);
    }

    private static IReadOnlyList<string> RequiredFiles(string platform) => platform == "linux"
        ?
        [
            "install-execution-node.sh", "CSweet.RuntimeHost", "CSweet.ExecutionNode",
            "CSweet.AgentRuntime.Firecracker.Helper", "runtime-manifest.json",
            "csweet-runtime-host.service", "csweet-execution-node.service", "uninstall-execution-node.sh",
            Path.Combine("firecracker", "firecracker"), Path.Combine("firecracker", "jailer"),
            Path.Combine("firecracker", "vmlinux"), Path.Combine("firecracker", "initrd.img")
        ]
        :
        [
            "install-execution-node.sh", "CSweet.RuntimeHost", "CSweet.ExecutionNode",
            "CSweet.AgentRuntime.AppleVirtualization.Helper", "runtime-manifest.json",
            "com.csweet.runtimehost.plist", "com.csweet.executionnode.plist", "uninstall-execution-node.sh",
            Path.Combine("apple-virtualization", "vmlinux")
        ];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
