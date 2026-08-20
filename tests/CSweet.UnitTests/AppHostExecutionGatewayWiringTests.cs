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
    public void ExecutionGateway_UsesAppHostOwnedCertificateAndPublishesItsPin()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src/CSweet.AppHost/Program.cs"));

        Assert.Contains(
            ".WithEnvironment(\"ASPNETCORE_Kestrel__Certificates__Default__Path\", executionGatewayCertificate.Path)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"CSweet__ExecutionGateway__PublicCertificateSha256\", executionGatewayCertificate.Sha256)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"CSweet__ExecutionGateway__BootstrapUrl\", executionGatewayBootstrapEndpoint)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("development-execution-gateway.pfx", source, StringComparison.Ordinal);
        Assert.Contains("existing.HasPrivateKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionGateway_DevelopmentPortsAvoidTheWindowsEphemeralRange()
    {
        var launchSettings = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src/CSweet.ExecutionGateway/Properties/launchSettings.json"));

        Assert.Contains("https://localhost:47082;http://localhost:47083", launchSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("54782", launchSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("54783", launchSettings, StringComparison.Ordinal);
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
