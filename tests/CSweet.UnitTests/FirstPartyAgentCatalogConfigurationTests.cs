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

    [Fact]
    public void SoftwareArchitect_IsAvailableFromItsStandaloneRepository()
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
        var architect = agents.EnumerateArray().Single(x =>
            x.GetProperty("AgentId").GetString() ==
            "com.csweet.software-architect");

        Assert.Equal(
            "Available",
            architect.GetProperty("Availability").GetString());
        Assert.Equal(
            "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareArchitect",
            architect.GetProperty("RepositoryUrl").GetString());
        Assert.Contains(
            architect.GetProperty("Capabilities").EnumerateArray(),
            capability => capability.GetString() == "engineering.architecture");
    }

    [Fact]
    public void SoftwareQa_IsAvailableFromItsStandaloneRepository()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "src",
            "CSweet.Api",
            "first-party-agents.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var qa = document.RootElement
            .GetProperty("CSweet")
            .GetProperty("Marketplace")
            .GetProperty("FirstPartyAgents")
            .EnumerateArray()
            .Single(x => x.GetProperty("AgentId").GetString() ==
                         "com.csweet.software-qa");

        Assert.Equal("Available", qa.GetProperty("Availability").GetString());
        Assert.Equal(
            "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareQA",
            qa.GetProperty("RepositoryUrl").GetString());
        Assert.Contains(
            qa.GetProperty("Capabilities").EnumerateArray(),
            capability => capability.GetString() == "engineering.quality");
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
