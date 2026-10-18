using CSweet.Compute.HyperV;

namespace CSweet.UnitTests;

public sealed class ComputeHyperVScriptTests
{
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
