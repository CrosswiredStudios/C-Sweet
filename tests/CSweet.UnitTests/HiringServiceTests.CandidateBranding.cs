using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RecommendationReadsRefreshMatchingFirstPartyBrandingWithoutChangingSavedSelection(bool installationView, bool sameIdentity)
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db);
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var selected = new AvailableAgent("first-party:73a7b05e276c47c5bb17cd548fd40c4c", "com.csweet.video-game-director",
            AgentCatalogSource.FirstPartyCatalog, [], AgentAvailabilityState.AvailableToInstall, null,
            "Video Game Director", "Saved summary", "C-Sweet", "Creative", [], [], ["game-direction.game-vision.v1"],
            0m, "USD", null, 0, null, "https://github.com/CrosswiredStudios/CSweet.Agent.Director.VideoGame", 1, "First party");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var saved = JsonSerializer.Serialize(selected, jsonOptions);
        plan.SelectedCatalogAgentJson = saved;
        plan.SelectionRationale = "Original recommendation evidence";
        await db.SaveChangesAsync();
        var current = selected with
        {
            AgentId = sameIdentity ? selected.AgentId : "com.example.different-agent",
            Name = "Marcus Vale", ImageUrl = "_content/CSweet.UI/images/agents/video-game-director-v1.png",
            CompanyLogoUrl = "_content/CSweet.UI/images/csweet-icon.svg", AccentColor = "#365F68",
            // Changes outside presentation must not silently alter a saved recommendation.
            Price = 100m, Capabilities = ["changed.capability"], RepositoryUrl = "https://example.test/changed"
        };
        var service = new HiringService(db, new RecordingOrganizationUserService(db), new TestAuditEventWriter(),
            agentCatalog: new RecordingAgentCatalog(current));
        var cards = installationView
            ? await service.ListRecommendationsForInstallationAsync(f.Organization, f.Installation)
            : await service.ListRecommendationsAsync(f.Organization);
        var card = Assert.Single(cards);
        var displayed = JsonSerializer.Deserialize<AvailableAgent>(card.SelectedCatalogAgentJson!, jsonOptions)!;
        Assert.Equal(sameIdentity ? "Marcus Vale" : selected.Name, displayed.Name);
        Assert.Equal(sameIdentity ? current.ImageUrl : null, displayed.ImageUrl);
        Assert.Equal(selected.Price, displayed.Price);
        Assert.Equal(selected.Capabilities, displayed.Capabilities);
        Assert.Equal(selected.RepositoryUrl, displayed.RepositoryUrl);
        Assert.Equal(selected.AgentReference, displayed.AgentReference);
        Assert.Equal(plan.SelectionRationale, card.SelectionRationale);
        Assert.Equal(saved, (await db.WorkforcePlans.AsNoTracking().SingleAsync(x => x.Id == plan.Id)).SelectedCatalogAgentJson);
    }
}
