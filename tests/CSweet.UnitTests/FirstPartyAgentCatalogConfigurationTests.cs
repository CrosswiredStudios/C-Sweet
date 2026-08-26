using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Marketplace;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class FirstPartyAgentCatalogConfigurationTests
{
    [Fact]
    public void VideoGameCreativeDirector_IsCanonicalCreativeDirectorWithGameSpecializations()
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "CSweet.Api", "first-party-agents.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var creativeDirector = document.RootElement
            .GetProperty("CSweet")
            .GetProperty("Marketplace")
            .GetProperty("FirstPartyAgents")
            .EnumerateArray()
            .Single(x => x.GetProperty("AgentId").GetString() ==
                         "com.csweet.video-game-creative-director");

        Assert.Equal("Available", creativeDirector.GetProperty("Availability").GetString());
        Assert.Equal("creative-director", creativeDirector.GetProperty("RoleKey").GetString());
        Assert.Equal("Creative Director", creativeDirector.GetProperty("RoleName").GetString());
        Assert.Equal(
            ["video-game-development", "game-creative-direction"],
            creativeDirector.GetProperty("SpecializationKeys").EnumerateArray()
                .Select(x => x.GetString()!).ToArray());
        Assert.Contains(
            creativeDirector.GetProperty("Keywords").EnumerateArray(),
            keyword => keyword.GetString() == "video games");
        Assert.Equal(
            "https://github.com/CrosswiredStudios/CSweet.Agent.CreativeDirector.VideoGame",
            creativeDirector.GetProperty("RepositoryUrl").GetString());
    }

    [Fact]
    public async Task FirstPartyProvider_MapsCreativeDirectorRoleAndVideoGameSpecializations()
    {
        var provider = new FirstPartyAgentCatalogProvider(Options.Create(new MarketplaceOptions
        {
            FirstPartyAgents = [new FirstPartyMarketplaceAgentOptions
            {
                Id = Guid.NewGuid(),
                AgentId = "com.csweet.video-game-creative-director",
                ListingSlug = "video-game-creative-director",
                Name = "Video Game Creative Director",
                Summary = "Leads video-game vision.",
                Category = "Creative",
                RoleKey = "creative-director",
                RoleName = "Creative Director",
                SpecializationKeys = ["video-game-development", "game-creative-direction"],
                Keywords = ["video games"],
                RepositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.CreativeDirector.VideoGame"
            }]
        }));

        var result = await provider.SearchAsync(null, new AvailableAgentSearchQuery(
            RoleCategoryKey: "creative-director",
            PreferredSpecializationKeys: ["video-game-development"]));

        var agent = Assert.Single(result.Agents);
        Assert.Equal(["creative-director"], agent.RoleCategoryKeys);
        Assert.Equal(["video-game-development", "game-creative-direction"], agent.SpecializationKeys);
    }

    [Fact]
    public void SoftwareProductManager_IsAvailableFromItsStandaloneRepository()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "src",
            "CSweet.Api",
            "first-party-agents.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var productManager = document.RootElement
            .GetProperty("CSweet")
            .GetProperty("Marketplace")
            .GetProperty("FirstPartyAgents")
            .EnumerateArray()
            .Single(x => x.GetProperty("AgentId").GetString() ==
                         "com.csweet.product-manager");

        Assert.Equal("Available", productManager.GetProperty("Availability").GetString());
        Assert.Equal(
            "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareProductManager",
            productManager.GetProperty("RepositoryUrl").GetString());
    }

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
