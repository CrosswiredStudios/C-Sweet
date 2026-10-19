using CSweet.Compute.HyperV;

namespace CSweet.UnitTests;

public sealed class ComputeHyperVScriptTests
{
    [Theory]
    [InlineData("removed-vm", true)]
    [InlineData("other-vm", false)]
    [InlineData("unrelated-owner", false)]
    [InlineData("existing-vm", false)]
    public async Task Removed_workload_reclaims_only_the_exact_absent_vm_worker_ownership(string scenario, bool allowed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "csweet-removed-workload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Memory.dmp"), "fixture");
            var vm = Guid.Parse("690f9aad-805b-457f-820c-4ba10753d8b1");
            var script = """
                function Import-Module {}
                function Get-VM { if ($env:CSWEET_TEST_SCENARIO -eq 'existing-vm') { [pscustomobject]@{ Id=[guid]$env:CSWEET_COMPUTE_VM_ID } } }
                function Get-Acl {
                    param($LiteralPath)
                    $acl=[Security.AccessControl.FileSecurity]::new()
                    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($env:CSWEET_TEST_OWNER))
                    return $acl
                }
                $script:reclaimed=0
                function Set-Acl {
                    param($LiteralPath,$AclObject)
                    if ($AclObject.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { throw 'Owner was not reclaimed.' }
                    $rules=$AclObject.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])
                    if (@($rules | Where-Object { $_.IdentityReference.Value -notin @('S-1-5-32-544','S-1-5-18') }).Count -ne 0) { throw 'Unexpected access remains.' }
                    $script:reclaimed++
                }
                """ + "\n" + HyperVComputeDriver.ProtectRemovedWorkloadScript + "\n[Console]::Write($script:reclaimed)";
            var parameters = new Dictionary<string, string>
            {
                ["CSWEET_COMPUTE_VM_ID"] = vm.ToString("D"),
                ["CSWEET_COMPUTE_WORKLOAD_PATH"] = root,
                ["CSWEET_TEST_SCENARIO"] = scenario,
                ["CSWEET_TEST_OWNER"] = scenario switch
                {
                    "other-vm" => HyperVWorkloadProtection.WorkerIdentity(Guid.NewGuid()).Value,
                    "unrelated-owner" => "S-1-5-32-545",
                    _ => HyperVWorkloadProtection.WorkerIdentity(vm).Value
                }
            };
            var runner = new HyperVCommandRunner();
            if (allowed) Assert.Equal("2", await runner.RunAsync(script, parameters, default));
            else await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(script, parameters, default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Fixed_network_rules_use_distinct_priorities_for_every_adapter_setting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var functions = HyperVComputeDriver.CreateScript[..HyperVComputeDriver.CreateScript.IndexOf("Import-Module Hyper-V", StringComparison.Ordinal)];
        var script = """
            $script:weights = @{}
            function Get-VMSwitch { [pscustomobject]@{ SwitchType='Internal'; Name='Default Switch'; Id=[guid]'11111111-1111-1111-1111-111111111111' } }
            function Get-NetIPAddress { [pscustomobject]@{ AddressState='Preferred'; IPAddress='172.30.240.1' } }
            function Add-VMNetworkAdapter {}
            function Get-VMNetworkAdapter { [pscustomobject]@{ Name='CSweet Outbound'; SwitchId=[guid]'11111111-1111-1111-1111-111111111111'; MacAddressSpoofing='Off' } }
            function Set-VMNetworkAdapter {}
            function Add-VMNetworkAdapterExtendedAcl {
                param($VMNetworkAdapter,$Action,$Direction,$LocalPort,$RemotePort,$RemoteIPAddress,$Protocol,$Weight,$Stateful,$ErrorAction)
                if ($script:weights.ContainsKey([int]$Weight)) { throw 'Duplicate Hyper-V setting priority.' }
                $script:weights[[int]$Weight]=$Direction
            }
            $env:CSWEET_COMPUTE_NETWORK='OutboundOnly'
            """ + "\n" + functions + "\nAdd-ComputeOutboundNetwork ([pscustomobject]@{}); Assert-ComputeNetwork ([pscustomobject]@{}); [Console]::Write($script:weights.Count)";
        var output = await new HyperVCommandRunner().RunAsync(script, new Dictionary<string,string>(), default);
        Assert.Equal("17", output);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("control")]
    [InlineData("observe")]
    [InlineData("discover")]
    [InlineData("removed-workload")]
    public async Task Fixed_commands_parse_in_the_actual_helper_without_executing_hypervisor_operations(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = name switch
        {
            "create" => HyperVComputeDriver.CreateScript,
            "control" => HyperVComputeDriver.ControlScript,
            "observe" => HyperVComputeDriver.ObserveScript,
            "removed-workload" => HyperVComputeDriver.ProtectRemovedWorkloadScript,
            _ => HyperVComputeDriver.DiscoverScript
        };
        var output = await new HyperVCommandRunner().RunAsync(
            "$tokens=$null;$errors=$null;[System.Management.Automation.Language.Parser]::ParseInput($env:CSWEET_PARSE_SOURCE,[ref]$tokens,[ref]$errors) | Out-Null; if ($errors.Count -gt 0) { throw 'Invalid fixed script' }; [Console]::Write('parsed')",
            new Dictionary<string, string> { ["CSWEET_PARSE_SOURCE"] = script }, default);
        Assert.Equal("parsed", output);
    }
}
