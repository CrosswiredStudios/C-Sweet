using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiringPolicy_OwnDeferredHireRevalidatesCurrentAuthority(bool revoke)
    {
        await using var db = CreateDb();
        var definitions = new RecordingDefinitionService { IsAvailableForHire = false };
        var f = await AutonomyFixtureAsync(db, requireConfiguration: false, definitions: definitions);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation, HiringPublisherPreference.FirstPartyOnly),
            0, "Hire automatically");
        var recommendation = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own product outcomes", null, [], null, "deferred-leadership"));

        Assert.Equal("Building", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, recommendation.Id)).Status);
        var hiringCard = await db.CoreWorkTasks.SingleAsync(x => x.CreationIdempotencyKey == $"chief-work:hire:{recommendation.Id:N}");
        Assert.False(hiringCard.IsExecutable);
        Assert.Equal(WorkTaskStatus.Running, hiringCard.Status);
        using (var snapshot = JsonDocument.Parse((await db.StaffingActionProposals.SingleAsync()).PayloadJson))
            Assert.Equal(hiringCard.Id, snapshot.RootElement.GetProperty("auditWorkItemId").GetGuid());
        Assert.Empty(await db.HiringRecommendationFulfillments.ToListAsync());
        if (revoke)
            await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(), 1, "Review each hire");
        definitions.CompleteBuild();
        Assert.True(await f.Service.ProcessNextAsync("leadership-test"));

        var operation = await db.AgentHireOperations.SingleAsync();
        Assert.Equal(revoke ? AgentHireOperationStatus.Failed : AgentHireOperationStatus.Succeeded, operation.Status);
        Assert.Equal(revoke ? 0 : 1, await db.HiringRecommendationFulfillments.CountAsync());
        Assert.Equal(revoke ? WorkTaskStatus.Blocked : WorkTaskStatus.Completed, hiringCard.Status);
        Assert.True(await db.WorkItemActivities.AnyAsync(x => x.WorkItemId == hiringCard.Id && x.EventType == "chief.hiring.operation"));
        if (revoke) Assert.Contains("revoked", operation.Error);
    }

    [Theory]
    [InlineData(AutomaticHiringLevel.BroadDelegation, AgentCatalogSource.FirstPartyCatalog)]
    [InlineData(AutomaticHiringLevel.WithinLimits, AgentCatalogSource.FirstPartyCatalog)]
    [InlineData(AutomaticHiringLevel.ApprovedPackages, AgentCatalogSource.Installed)]
    public async Task HiringPolicy_OwnLeadershipSuggestionUsesSavedAuthorityAndReportsToOwner(
        AutomaticHiringLevel level, AgentCatalogSource source)
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, source, requireConfiguration: false);
        // A suggestion can predate the owner's choice to enable automatic hiring.
        var recommendation = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own product outcomes", null, [], null, "own-leadership") { RoleKey = "product-manager" });
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, level,
                level == AutomaticHiringLevel.ApprovedPackages ? HiringPublisherPreference.PreferFirstParty : HiringPublisherPreference.FirstPartyOnly,
                MaximumHireCost: 0, MaximumTotalCost: 0, Currency: "USD",
                AllowedCapabilities: ["platform.llm.chat-stream.v1", "platform.business-profile.read.v1"], AllowedNetworkAccess: []),
            0, "Hire automatically");

        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, recommendation.Id);

        Assert.True(result.Status == "Succeeded", result.Message);
        var workflow = await db.StaffingActionProposals.SingleAsync();
        Assert.Equal(f.Installation, workflow.DelegatedInstallationId);
        using var snapshot = JsonDocument.Parse(workflow.PayloadJson);
        Assert.Equal(f.Owner, snapshot.RootElement.GetProperty("reportsToOrganizationUserId").GetGuid());
        Assert.Null((await db.WorkforcePlans.SingleAsync()).SourceResourceChangeRequestId);
        Assert.Empty(await db.ResourceChangeRequests.ToListAsync());
        Assert.Empty(await db.HiringPlanDelegations.ToListAsync());
        Assert.Single(await db.HiringRecommendationFulfillments.ToListAsync());
        Assert.Equal("Succeeded", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, recommendation.Id)).Status);
        Assert.Single(await db.HiringRecommendationFulfillments.ToListAsync());
    }

    [Theory]
    [InlineData(HiringSelectionMode.ChooseCandidates)]
    [InlineData(HiringSelectionMode.RecommendCandidates)]
    public async Task HiringPolicy_OwnSuggestionStillRequiresExplicitAutomaticMode(HiringSelectionMode mode)
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(mode, AutomaticHiringLevel.BroadDelegation), 0, "Review hires");
        var recommendation = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own product outcomes", null, [], null, "own-leadership"));

        Assert.Equal("NeedsReview", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, recommendation.Id)).Status);
        Assert.Empty(await db.StaffingActionProposals.ToListAsync());
        Assert.Empty(await db.HiringRecommendationFulfillments.ToListAsync());
    }

    [Theory]
    [InlineData(AgentCatalogSource.Marketplace, 0, false, "No eligible candidate")]
    [InlineData(AgentCatalogSource.FirstPartyCatalog, null, false, "unknown")]
    [InlineData(AgentCatalogSource.FirstPartyCatalog, 0, true, "configuration")]
    public async Task HiringPolicy_OwnSuggestionPreservesPublisherCostAndConfigurationChecks(
        AgentCatalogSource source, int? price, bool requireConfiguration, string reason)
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, source, price, requireConfiguration);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation, HiringPublisherPreference.FirstPartyOnly),
            0, "First-party automatic hiring");
        var recommendation = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own product outcomes", null, [], null, "own-leadership"));

        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, recommendation.Id);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Contains(reason, result.Message);
        Assert.Empty(await db.HiringRecommendationFulfillments.ToListAsync());
    }

    [Fact]
    public async Task HiringPolicy_OwnSuggestionCannotBypassTeamPlanApproval()
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = f.Organization, Name = "Product", TeamKey = "product" };
        db.OrganizationTeams.Add(team);
        await db.SaveChangesAsync();
        var recommendation = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own team outcomes", null, [], null, "team-staffing") { TeamId = team.Id });

        Assert.Equal("NeedsReview", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, recommendation.Id)).Status);
        Assert.Empty(await db.StaffingActionProposals.ToListAsync());
        Assert.Empty(await db.HiringRecommendationFulfillments.ToListAsync());
    }
}
