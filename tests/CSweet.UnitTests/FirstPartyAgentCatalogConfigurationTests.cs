using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class FirstPartyAgentCatalogConfigurationTests
{
    [Fact]
    public void SoftwareDeveloper_IsConnectionIndependentAndInstallable()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "src",
            "CSweet.Api",
            "first-party-agents.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var agents = document.RootElement
            .GetProperty("CSweet")
            .GetProperty("Marketplace")
            .GetProperty("FirstPartyAgents");
        var developer = agents.EnumerateArray().Single(x =>
            x.GetProperty("AgentId").GetString() ==
            "com.csweet.software-developer");

        Assert.Equal(
            "Available",
            developer.GetProperty("Availability").GetString());
        Assert.Equal(
            "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareDeveloper",
            developer.GetProperty("RepositoryUrl").GetString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
