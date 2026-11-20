using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using CSweet.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Fact]
    public async Task AutomaticHire_DependencyFailureDuringCapabilityAssessmentIsNotNoSuitableAgent()
    {
        await using var db = CreateDb(); var preview = new FailingHiringPreview(true);
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var role = await db.ResourceChangeRoles.SingleAsync(); role.RequiredCapabilitiesJson = "[\"platform.artifact.read.v1\"]";
        await db.SaveChangesAsync();
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("Queued", result.Status);
        Assert.Equal("WaitingDependency", DependencyWaits.Read(await db.PluginOperationalStates.SingleAsync(x => x.Kind == DependencyWaits.Kind)).Status);
        Assert.False(Assert.Single(await f.Service.ListRecommendationsAsync(f.Organization)).NoSuitableAgentFound);
        Assert.Equal(1, preview.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticHire_PersistsFailureAndDoesNotConsumeAgentRetries(bool rateLimited)
    {
        await using var db = CreateDb();
        var preview = new FailingHiringPreview(rateLimited);
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal(rateLimited ? "Queued" : "NeedsReview", result.Status);
        Assert.Equal(1, preview.Calls);
        var state = DependencyWaits.Read(await db.PluginOperationalStates.SingleAsync(x => x.Kind == DependencyWaits.Kind));
        Assert.Equal(rateLimited ? "WaitingDependency" : "NeedsReview", state.Status);
        Assert.Equal(rateLimited ? "dependency.rate_limited" : "hiring.review_required", state.Code);
        var card = Assert.Single(await f.Service.ListRecommendationsAsync(f.Organization));
        Assert.Equal(preview.Message, card.AutomaticHiringBlockReason);
        Assert.Equal(state.RetryAt, card.AutomaticHiringRetryAt);
        Assert.Equal(!rateLimited, OverviewAttentionItems.IsActionable(card));
        var receipt = await db.CoreWorkTasks.SingleAsync(x => x.CreationIdempotencyKey == $"chief-work:hire:{plan.Id:N}");
        Assert.Equal(rateLimited ? CSweet.Domain.Core.WorkTaskStatus.Running : CSweet.Domain.Core.WorkTaskStatus.Blocked, receipt.Status);
        var todos = new PersonalTodoService(db, TimeProvider.System);
        var grants = new ScopedActionAuthorizationService(db);
        var blocked = new BlockedWorkAttentionReader(db, todos, TimeProvider.System, new(db, todos, grants, new(db, grants, TimeProvider.System)));
        Assert.Empty(await blocked.ReadAsync(f.Organization, new(f.Owner, null)));
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.EventType.Contains("hiring-recommendations"));
        Assert.Empty(await db.AgentHireOperations.ToListAsync());
        await f.Service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id);
        await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        if (rateLimited) await f.Service.RetryAutomaticHiringAsync(f.Organization, plan.Id, f.Application);
        Assert.False(await f.Service.RecoverNextHiringDependencyAsync());
        Assert.Equal(1, preview.Calls);
    }

    [Fact]
    public async Task AutomaticHire_DueWaitRecoversOnceAndRevalidatesOwnerPolicy()
    {
        await using var db = CreateDb();
        var preview = new FailingHiringPreview(true);
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        var row = await db.PluginOperationalStates.SingleAsync(x => x.Kind == DependencyWaits.Kind);
        var wait = DependencyWaits.Read(row);
        await DependencyWaits.SaveAsync(db, f.Organization, f.Installation, wait with { RetryAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        // A saved due time is not permission to commit a hire after delegation is revoked.
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.RecommendCandidates), 1, "Review hires");
        Assert.True(await f.Service.RecoverNextHiringDependencyAsync());
        Assert.False(await f.Service.RecoverNextHiringDependencyAsync());
        Assert.Equal(1, preview.Calls);
        Assert.Empty(await db.AgentHireOperations.ToListAsync());
        Assert.Null(row.AvailableAt);
    }

    [Fact]
    public async Task AutomaticHire_RetryRequiresOwnerAndPreservesManualSelection()
    {
        await using var db = CreateDb();
        var preview = new FailingHiringPreview(true);
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RetryAutomaticHiringAsync(f.Organization, plan.Id, Guid.NewGuid()));
        await f.Service.ChooseRoleManuallyAsync(f.Organization, plan.Id, f.Application);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RetryAutomaticHiringAsync(f.Organization, plan.Id, f.Application));
        Assert.Equal(1, preview.Calls);
        var row = await db.PluginOperationalStates.SingleAsync(x => x.Kind == DependencyWaits.Kind);
        Assert.Null(row.AvailableAt);
        Assert.False(await f.Service.RecoverNextHiringDependencyAsync());
        Assert.Equal("Cancelled", DependencyWaits.Read(row).Status);
        Assert.Equal(1, preview.Calls);
    }

    private sealed class FailingHiringPreview(bool rateLimited) : IAgentImportPreviewService
    {
        public int Calls;
        public string Message => rateLimited ? "GitHub temporarily rate-limited repository access." : "Package settings need owner review.";
        public Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (rateLimited) throw new AgentImportPreviewException(Message) { IsDependencyFailure = true, IsRateLimited = true,
                RetryAt = DateTimeOffset.UtcNow.AddMinutes(10) };
            throw new InvalidOperationException(Message);
        }
    }

    [Fact]
    public async Task AutomaticHire_DueDependencyResumesAndFulfillsExactlyOneSeat()
    {
        await using var db = CreateDb();
        var preview = new RecoveringHiringPreview(new RecordingImportPreview(db, "https://github.com/example/product-manager"));
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire automatically");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        Assert.Equal("Queued", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        var row = await db.PluginOperationalStates.SingleAsync(x => x.Kind == DependencyWaits.Kind);
        await DependencyWaits.SaveAsync(db, f.Organization, f.Installation, DependencyWaits.Read(row) with { RetryAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync(); preview.Fail = false;
        Assert.True(await f.Service.RecoverNextHiringDependencyAsync());
        Assert.False(await f.Service.RecoverNextHiringDependencyAsync());
        Assert.Equal(1, plan.FulfilledHeadcount);
        Assert.Equal("Succeeded", DependencyWaits.Read(row).Status);
        Assert.Null(row.AvailableAt);
        Assert.Empty(await f.Service.ListRecommendationsAsync(f.Organization));
        Assert.Single(await db.AgentHireOperations.ToListAsync());
        var repeated = await f.Service.RetryAutomaticHiringAsync(f.Organization, plan.Id, f.Application);
        Assert.NotNull(repeated); Assert.Equal(0, repeated.RemainingHeadcount);
    }

    [Fact]
    public async Task HiringReview_PublicWebAccessIsReviewableAndBoundToTheApprovedSnapshot()
    {
        await using var db = CreateDb();
        var preview = new RecoveringHiringPreview(new RecordingImportPreview(db, "https://github.com/example/product-manager")) { Fail = false, PublicWeb = true };
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        var review = await f.Service.PreviewMarketplaceHireAsync(f.Organization, f.Application,
            new("catalog:product-manager", "Product Manager", "Test employee", f.Owner, "review-public-web"));
        Assert.Contains("all-public", review.NetworkAccess);
        var workflow = await db.StaffingActionProposals.SingleAsync();
        using var snapshot = JsonDocument.Parse(workflow.PayloadJson);
        Assert.Contains("all-public", snapshot.RootElement.GetProperty("embeddedAgent").GetProperty("networkAccess").EnumerateArray().Select(x => x.GetString()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.StartAsync(f.Organization, review.WorkflowId, Guid.NewGuid(), new("not-owner")));
        var result = await f.Service.StartAsync(f.Organization, review.WorkflowId, f.Application, new("owner-approved"));
        Assert.Equal("Succeeded", result?.Status);
    }

    private sealed class RecoveringHiringPreview(IAgentImportPreviewService inner) : IAgentImportPreviewService
    {
        public bool Fail = true; public bool PublicWeb;
        public async Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken ct = default)
        {
            if (Fail) throw new AgentImportPreviewException("GitHub temporarily rate-limited repository access.")
                { IsDependencyFailure = true, IsRateLimited = true, RetryAt = DateTimeOffset.UtcNow.AddMinutes(5) };
            var value = await inner.PreviewAsync(request, ct);
            return value with { ConfigurationFields = [], RequestedNetworkAccess = PublicWeb ? ["all-public"] : value.RequestedNetworkAccess };
        }
    }

    [Fact]
    public async Task AutomaticHiring_PublicWebRequestCannotExceedDelegatedNetworkLimits()
    {
        await using var db = CreateDb();
        var preview = new RecoveringHiringPreview(new RecordingImportPreview(db, "https://github.com/example/product-manager")) { Fail = false, PublicWeb = true };
        var f = await AutonomyFixtureAsync(db, importPreviewOverride: preview);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.WithinLimits, MaximumHireCost: 0, MaximumTotalCost: 0,
                Currency: "USD", AllowedCapabilities: [], AllowedNetworkAccess: []), 0, "No network delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Contains("permissions outside", result.Message);
        var card = Assert.Single(await f.Service.ListRecommendationsAsync(f.Organization));
        Assert.Equal("NeedsReview", card.AutomaticHiringStatus);
        Assert.Equal("hiring.review_required", card.AutomaticHiringErrorCode);
        Assert.Equal(0, plan.FulfilledHeadcount);
        Assert.Empty(await db.AgentHireOperations.ToListAsync());
        Assert.Equal("NeedsReview", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
    }
}
