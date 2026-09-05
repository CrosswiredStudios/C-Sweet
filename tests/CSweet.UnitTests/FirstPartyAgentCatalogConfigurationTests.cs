using CSweet.Application.Agents;
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

    [Theory]
    [InlineData("com.csweet.infrastructure-engineer.namecheap", "https://github.com/CrosswiredStudios/CSweet.Agent.InfrastructureEngineer.Namecheap")]
    [InlineData("com.csweet.chief-of-staff", "https://github.com/CrosswiredStudios/CSweet.Agent.ChiefOfStaff")]
    [InlineData("com.csweet.product-manager", "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareProductManager")]
    [InlineData("com.csweet.video-game-creative-director", "https://github.com/CrosswiredStudios/CSweet.Agent.CreativeDirector.VideoGame")]
    [InlineData("com.csweet.video-game-producer", "https://github.com/CrosswiredStudios/CSweet.Agent.Producer.VideoGame")]
    [InlineData("com.csweet.software-developer", "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareDeveloper")]
    [InlineData("com.csweet.software-architect", "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareArchitect")]
    [InlineData("com.csweet.software-qa", "https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareQA")]
    [InlineData("com.csweet.video-game-art-director", "https://github.com/CrosswiredStudios/CSweet.Agent.ArtDirector.VideoGame")]
    [InlineData("com.csweet.video-game-artist", "https://github.com/CrosswiredStudios/CSweet.Agent.Artist.VideoGame")]
    [InlineData("com.csweet.video-game-audio-designer", "https://github.com/CrosswiredStudios/CSweet.Agent.AudioDesigner.VideoGame")]
    [InlineData("com.csweet.video-game-build-release-engineer", "https://github.com/CrosswiredStudios/CSweet.Agent.BuildReleaseEngineer.VideoGame")]
    [InlineData("com.csweet.video-game-engineer", "https://github.com/CrosswiredStudios/CSweet.Agent.Engineer.VideoGame")]
    [InlineData("com.csweet.video-game-designer", "https://github.com/CrosswiredStudios/CSweet.Agent.GameDesigner")]
    [InlineData("com.csweet.video-game-level-designer", "https://github.com/CrosswiredStudios/CSweet.Agent.LevelDesigner.VideoGame")]
    [InlineData("com.csweet.video-game-narrative-designer", "https://github.com/CrosswiredStudios/CSweet.Agent.NarrativeDesigner.VideoGame")]
    [InlineData("com.csweet.video-game-playtest-researcher", "https://github.com/CrosswiredStudios/CSweet.Agent.PlaytestResearcher.VideoGame")]
    [InlineData("com.csweet.video-game-qa", "https://github.com/CrosswiredStudios/CSweet.Agent.QA.VideoGame")]
    [InlineData("com.csweet.video-game-technical-artist", "https://github.com/CrosswiredStudios/CSweet.Agent.TechnicalArtist.VideoGame")]
    [InlineData("com.csweet.video-game-technical-director", "https://github.com/CrosswiredStudios/CSweet.Agent.TechnicalDirector.VideoGame")]
    [InlineData("com.csweet.video-game-ui-ux-accessibility-designer", "https://github.com/CrosswiredStudios/CSweet.Agent.UiUxAccessibilityDesigner.VideoGame")]
    [InlineData("com.csweet.youtube-account-manager", "https://github.com/CrosswiredStudios/CSweet.Agent.YouTubeAccountManager")]
    public async Task FirstPartyAgent_ResolvesHostedRepository(string agentId, string repositoryUrl)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "CSweet.Api", "first-party-agents.json")));
        var options = document.RootElement.GetProperty("CSweet").GetProperty("Marketplace")
            .Deserialize<MarketplaceOptions>()!;
        Assert.Equal(options.FirstPartyAgents.Count, options.FirstPartyAgents.Select(x => x.Id).Distinct().Count());
        Assert.Equal(options.FirstPartyAgents.Count, options.FirstPartyAgents.Select(x => x.AgentId).Distinct().Count());
        var provider = new FirstPartyAgentCatalogProvider(Options.Create(options));
        var result = await provider.SearchAsync(null, new AvailableAgentSearchQuery());
        var agent = Assert.Single(result.Agents, x => x.AgentId == agentId);
        Assert.Equal(repositoryUrl, agent.RepositoryUrl);
        Assert.Equal(AgentAvailabilityState.AvailableToInstall, agent.Availability);
        Assert.False(string.IsNullOrWhiteSpace(agent.RoleKey));
        var resolved = await provider.ResolveAsync(null, agent.AgentReference);
        Assert.NotNull(resolved);
        Assert.Equal(repositoryUrl, resolved.RepositoryUrl);
        Assert.Equal(AgentCatalogSource.FirstPartyCatalog, resolved.Source);
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
