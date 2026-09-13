using System.Diagnostics;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeHyperVTests
{
    private sealed class Runner : IHyperVCommandRunner
    {
        public Guid VmId { get; } = Guid.NewGuid();
        public string? Script { get; private set; }
        public IReadOnlyDictionary<string, string>? Parameters { get; private set; }
        public Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            Script = script; Parameters = parameters;
            return Task.FromResult(JsonSerializer.Serialize(new { id = VmId, state = "Off" }));
        }
    }
    private static HyperVMachineIdentity Identity => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData("windows", "MicrosoftWindows")]
    [InlineData("linux", "MicrosoftUEFICertificateAuthority")]
    public async Task Clean_machine_uses_one_private_os_disk_and_explicit_firmware(string os, string firmware)
    {
        var runner = new Runner(); var driver = new HyperVComputeDriver(runner); var identity = Identity;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "compute-command-contract"));
        var result = await driver.CreateAsync(identity, new(os, "x64", os + "-clean", new(2, 4096, 65536), 600),
            Path.Combine(root, "vm"), Path.Combine(root, "os.vhdx"), Path.Combine(root, "image.vhdx"), default);
        Assert.Equal(runner.VmId, result.Id);
        Assert.Equal(firmware, runner.Parameters!["CSWEET_COMPUTE_FIRMWARE"]);
        Assert.Equal(identity.Ownership, runner.Parameters["CSWEET_COMPUTE_OWNER"]);
        Assert.Contains("-NoVHD", runner.Script);
        Assert.Contains("$disk.Count -ne 1", runner.Script);
        Assert.Contains("Remove-VMNetworkAdapter", runner.Script);
        Assert.DoesNotContain("artifact.iso", runner.Script);
        Assert.DoesNotContain("scratch", runner.Script);
        Assert.DoesNotContain("Docker", runner.Script);
    }

    [Fact]
    public async Task Unsupported_network_is_rejected_before_any_host_command()
    {
        var runner = new Runner(); var driver = new HyperVComputeDriver(runner);
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.CreateAsync(Identity,
            new("linux", "x64", "ubuntu-clean", new(2, 4096, 65536), 600, Network: new(ComputeNetworkMode.OutboundOnly, true)),
            "unused", "unused", "unused", default));
        Assert.Null(runner.Script);
    }

    [Fact]
    public async Task Exact_vm_identity_and_ownership_are_checked_before_control_without_deleting_disks()
    {
        var runner = new Runner(); var driver = new HyperVComputeDriver(runner); var identity = Identity;
        await driver.ApplyAsync(identity, runner.VmId, InfrastructureActions.Stop, default);
        Assert.Equal(runner.VmId.ToString("D"), runner.Parameters!["CSWEET_COMPUTE_VM_ID"]);
        Assert.Contains("$vm.Notes -cne $env:CSWEET_COMPUTE_OWNER", runner.Script);
        Assert.DoesNotContain("SilentlyContinue", runner.Script);
        Assert.DoesNotContain("Remove-Item", runner.Script);
        Assert.DoesNotContain("File]::Delete", runner.Script);
        Assert.True(runner.Script!.IndexOf("ownership", StringComparison.Ordinal) < runner.Script.IndexOf("Stop-VM", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => driver.ObserveAsync(identity, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Helper_drains_large_output_without_retaining_more_than_its_budget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = await new HyperVCommandRunner().RunAsync("[Console]::Out.Write(('x' * 131072))", new Dictionary<string, string>(), default);
        Assert.Equal(65536, output.Length);
    }

    [Fact]
    public async Task Cancellation_waits_for_the_owned_helper_to_exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        var task = new HyperVCommandRunner().RunAsync("[IO.File]::WriteAllText($env:CSWEET_TEST_PID, [string]$PID); Start-Sleep -Seconds 30",
            new Dictionary<string, string> { ["CSWEET_TEST_PID"] = path }, cancellation.Token);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (new FileInfo(path).Length == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            var pid = int.Parse(await File.ReadAllTextAsync(path));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally
        {
            cancellation.Cancel();
            try { await task; } catch (OperationCanceledException) { }
            File.Delete(path);
        }
    }
}
