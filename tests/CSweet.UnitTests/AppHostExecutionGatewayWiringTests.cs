namespace CSweet.UnitTests;

public sealed class AppHostExecutionGatewayWiringTests
{
    [Fact]
    public void ExecutionGateway_ReceivesAgentHostBrokerEndpoint()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src/CSweet.AppHost/Program.cs"));
        var gatewayStart = source.IndexOf(
            "builder.AddProject<Projects.CSweet_ExecutionGateway>",
            StringComparison.Ordinal);
        var gatewayEnd = source.IndexOf("var executionGatewayEndpoint", gatewayStart, StringComparison.Ordinal);

        Assert.True(gatewayStart >= 0 && gatewayEnd > gatewayStart, "ExecutionGateway AppHost registration was not found.");
        var registration = source[gatewayStart..gatewayEnd];
        Assert.Contains(".WithReference(agentHostEndpoint)", registration, StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"CSweet__AgentRuntime__AgentHostBroker__BaseUrl\", agentHostEndpoint)",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(".WaitFor(agentHost)", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalOfficeSetup_UsesCurrentOfficeBootstrap()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src/CSweet.AppHost/Program.cs"));

        Assert.Contains(
            "\"CSweet.Office\", \"scripts\", \"windows\", \"Initialize-CSweetWindowsIsolationTest.ps1\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"CSweet.SatelliteOffice\", \"scripts\", \"windows\", \"Initialize-CSweetWindowsIsolationTest.ps1\"",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
