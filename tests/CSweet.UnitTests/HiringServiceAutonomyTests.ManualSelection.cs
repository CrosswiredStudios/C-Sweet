using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Fact]
    public async Task ManualSelection_IsOwnerOnlyRoleScopedAndSurvivesPolicyChangesAndRetries()
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var first = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own product", null, [], null, "first-role"));
        var second = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Operations Manager", "Own operations", null, [], null, "second-role"));
        Assert.All(await f.Service.ListRecommendationsAsync(f.Organization), x => Assert.True(x.AutomaticHiringEnabled));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ChooseRoleManuallyAsync(f.Organization, first.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ChooseRoleManuallyAsync(Guid.NewGuid(), first.Id, f.Application));
        var changed = await f.Service.ChooseRoleManuallyAsync(f.Organization, first.Id, f.Application);
        Assert.True(changed!.ManualSelectionRequested);
        Assert.False(changed.AutomaticHiringEnabled);
        Assert.Contains($"recommendationId={first.Id:D}", changed.HiringUrl);
        var savedAt = (await db.WorkforcePlans.SingleAsync(x => x.Id == first.Id)).ManualSelectionRequestedAt;
        await f.Service.ChooseRoleManuallyAsync(f.Organization, first.Id, f.Application);
        Assert.Equal(savedAt, (await db.WorkforcePlans.SingleAsync(x => x.Id == first.Id)).ManualSelectionRequestedAt);
        Assert.Equal(f.Owner, (await db.WorkforcePlans.SingleAsync(x => x.Id == first.Id)).ManualSelectionRequestedByOrganizationUserId);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 1, "Keep hiring other roles automatically");
        Assert.Equal(HiringSelectionMode.ChooseCandidates, (await f.Service.SelectCandidateAsync(f.Organization, f.Installation, first.Id)).Mode);
        Assert.Equal("NeedsReview", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, first.Id)).Status);
        Assert.Empty(await db.StaffingActionProposals.ToListAsync());
        var cards = await f.Service.ListRecommendationsAsync(f.Organization);
        Assert.False(cards.Single(x => x.Id == first.Id).AutomaticHiringEnabled);
        Assert.True(cards.Single(x => x.Id == second.Id).AutomaticHiringEnabled);
        Assert.Equal(HiringSelectionMode.Automatic, (await f.Service.ReadPolicyAsync(f.Organization, f.Installation)).Settings.Mode);
        Assert.Equal("Succeeded", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, second.Id)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ChooseRoleManuallyAsync(f.Organization, second.Id, f.Application));
    }

    [Fact]
    public async Task ManualSelection_CancelsPendingAutomaticHireButAllowsAnExplicitOwnerHire()
    {
        await using var db = CreateDb();
        var definitions = new RecordingDefinitionService { IsAvailableForHire = false };
        var f = await AutonomyFixtureAsync(db, requireConfiguration: false, definitions: definitions);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var plan = await f.Service.UpsertRecommendationAsync(f.Organization, f.Installation,
            new("Product Manager", "Own product", null, [], null, "building-role"));
        Assert.Equal("Building", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        await f.Service.ChooseRoleManuallyAsync(f.Organization, plan.Id, f.Application);
        Assert.Equal(ProposalStatus.Cancelled, (await db.StaffingActionProposals.SingleAsync()).Status);
        Assert.Equal(AgentHireOperationStatus.Cancelled, (await db.AgentHireOperations.SingleAsync()).Status);
        Assert.Equal(WorkTaskStatus.Cancelled, (await db.CoreWorkTasks.SingleAsync(x => x.CreationIdempotencyKey == $"chief-work:hire:{plan.Id:N}")).Status);
        Assert.Equal(0, (await db.WorkforcePlans.SingleAsync()).FulfilledHeadcount);
        definitions.CompleteBuild();
        Assert.False(await f.Service.ProcessNextAsync("stopped-role"));
        Assert.Equal("NeedsReview", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        var preview = await f.Service.PreviewMarketplaceHireAsync(f.Organization, f.Application,
            new("catalog:product-manager", "Product Manager", "Chosen manager", f.Owner, "owner-chosen-role") { RecommendationId = plan.Id });
        Assert.Equal("Succeeded", (await f.Service.StartAsync(f.Organization, preview.WorkflowId, f.Application,
            new("owner-confirmed-role")))!.Status);
        Assert.Single(await db.HiringRecommendationFulfillments.ToListAsync());
        Assert.Equal(1, (await db.WorkforcePlans.SingleAsync()).FulfilledHeadcount);
    }
}
