using CSweet.Compute.HyperV;

namespace CSweet.UnitTests;

public sealed class ComputeHyperVScriptTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("control")]
    [InlineData("observe")]
    [InlineData("discover")]
    public async Task Fixed_commands_parse_in_the_actual_helper_without_executing_hypervisor_operations(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = name switch
        {
            "create" => HyperVComputeDriver.CreateScript,
            "control" => HyperVComputeDriver.ControlScript,
            "observe" => HyperVComputeDriver.ObserveScript,
            _ => HyperVComputeDriver.DiscoverScript
        };
        var output = await new HyperVCommandRunner().RunAsync(
            "$tokens=$null;$errors=$null;[System.Management.Automation.Language.Parser]::ParseInput($env:CSWEET_PARSE_SOURCE,[ref]$tokens,[ref]$errors) | Out-Null; if ($errors.Count -gt 0) { throw 'Invalid fixed script' }; [Console]::Write('parsed')",
            new Dictionary<string, string> { ["CSWEET_PARSE_SOURCE"] = script }, default);
        Assert.Equal("parsed", output);
    }
}
