using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkInboxTests
{
    private static async Task<(AgentInstallation Installation, AgentRuntimeInstance Runtime)> ChiefFixtureAsync(
        CSweet.Infrastructure.Persistence.CSweetDbContext db, DateTimeOffset now)
    {
        var installation = Installation(now);
        var organization = Guid.NewGuid();
        installation.BusinessId = organization.ToString("D");
        var runtime = Runtime(installation, now);
        var chief = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization, DisplayName = "Evelyn",
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id };
        db.AddRange(installation, runtime, chief, new Organization { Id = organization, Name = "Chief audit test" },
            new LeadershipAssignment { Id = Guid.NewGuid(), OrganizationId = organization,
                OrganizationUserId = chief.Id, PositionKey = LeadershipPositionKeys.ChiefOfStaff });
        await db.SaveChangesAsync();
        return (installation, runtime);
    }

    [Fact]
    public async Task ChiefDeliveryHasACardBeforeClaimAndItsFirstInferenceIsBound()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var f = await ChiefFixtureAsync(db, clock.GetUtcNow());
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var work = await inbox.EnqueueAsync(f.Installation.BusinessId, f.Installation.Id, AgentWorkKind.Capability,
            "assistant.converse.v1", Json("{}"), "audit-first-call", clock.GetUtcNow().AddHours(1));
        var card = await db.CoreWorkTasks.SingleAsync();
        Assert.Equal(WorkTaskStatus.Ready, card.Status);
        Assert.False(card.IsExecutable);
        Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());

        var claim = await inbox.ClaimAsync(Session(f.Installation, f.Runtime), default);
        Assert.NotNull(claim);
        Assert.Equal(WorkTaskStatus.Running, card.Status);
        var context = await db.WorkExecutionContexts.SingleAsync();
        Assert.Equal(card.Id, context.WorkItemId);
        Assert.Equal(card.Id, context.RootWorkItemId);
        var receipt = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(f.Installation.BusinessId),
            AgentInstallationId = f.Installation.Id, ProviderStartedAt = clock.GetUtcNow() };
        await InferenceAttribution.CaptureAsync(db, receipt, work.Id, attemptNumber: claim.Attempt);
        Assert.Equal(card.Id, receipt.WorkItemId);
        Assert.Equal("Execution", receipt.AttributionKind);
        var unhintedReceipt = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(f.Installation.BusinessId),
            AgentInstallationId = f.Installation.Id, ProviderStartedAt = clock.GetUtcNow() };
        await ChiefWorkCards.BindInferenceAsync(db, unhintedReceipt, f.Runtime.Id, default);
        Assert.Equal(card.Id, unhintedReceipt.WorkItemId);
        Assert.Equal(work.Id, unhintedReceipt.AgentWorkItemId);

        await inbox.CompleteAsync(Session(f.Installation, f.Runtime), work.Id, claim.Attempt, claim.LeaseToken,
            new(true, Json("{}"), null), default);
        Assert.Equal(WorkTaskStatus.Completed, card.Status);
        Assert.Null(card.ClaimEventId);
        Assert.Single(await db.CoreWorkTasks.ToListAsync());
    }

    [Fact]
    public async Task ChiefRetryReusesCardAndRetainsBothAttemptBindings()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var f = await ChiefFixtureAsync(db, clock.GetUtcNow());
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var work = await inbox.EnqueueAsync(f.Installation.BusinessId, f.Installation.Id, AgentWorkKind.Event,
            "example.event.v1", Json("{}"), "audit-retry", clock.GetUtcNow().AddHours(1), sourceId: Guid.NewGuid().ToString("D"));
        var session = Session(f.Installation, f.Runtime);
        var first = (await inbox.ClaimAsync(session, default))!;
        await inbox.FailAsync(session, work.Id, first.Attempt, first.LeaseToken, "Temporary transport failure", default);
        var card = await db.CoreWorkTasks.SingleAsync();
        Assert.Equal(WorkTaskStatus.Ready, card.Status);
        clock.Advance(TimeSpan.FromSeconds(5));
        var retry = (await inbox.ClaimAsync(session, default))!;
        Assert.Equal(2, retry.Attempt);
        Assert.Single(await db.CoreWorkTasks.ToListAsync());
        Assert.Equal(2, await db.WorkItemActivities.CountAsync(x => x.WorkItemId == card.Id && x.EventType == "agent.ticket.execution.bound"));
        await inbox.CancelAsync(work.Id, "Owner stopped work");
        Assert.Equal(WorkTaskStatus.Cancelled, card.Status);
        Assert.Contains("Owner stopped", card.ResultSummary);
    }

    [Fact]
    public async Task ChiefNeverRunsTwoUncorrelatableCallbacksAndRejectsUnleasedTools()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var f = await ChiefFixtureAsync(db, clock.GetUtcNow());
        var organization = Guid.Parse(f.Installation.BusinessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ChiefWorkCards.RequireActiveAsync(db,
            organization, f.Installation.Id, f.Runtime.Id, default));
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        foreach (var key in new[] { "one", "two" })
            await inbox.EnqueueAsync(f.Installation.BusinessId, f.Installation.Id, AgentWorkKind.Capability,
                "assistant.converse.v1", Json("{}"), key, clock.GetUtcNow().AddHours(1));
        Assert.NotNull(await inbox.ClaimAsync(Session(f.Installation, f.Runtime), default));
        Assert.Null(await inbox.ClaimAsync(Session(f.Installation, f.Runtime), default));
        await ChiefWorkCards.RequireActiveAsync(db, organization, f.Installation.Id, f.Runtime.Id, default);
        Assert.Equal(2, await db.CoreWorkTasks.CountAsync());
    }

    [Fact]
    public async Task ChiefCannotDispatchWhenItsAuditCardIsArchived()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var f = await ChiefFixtureAsync(db, clock.GetUtcNow());
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        await inbox.EnqueueAsync(f.Installation.BusinessId, f.Installation.Id, AgentWorkKind.Capability,
            "assistant.converse.v1", Json("{}"), "archived-audit-card", clock.GetUtcNow().AddHours(1));
        (await db.CoreWorkTasks.SingleAsync()).ArchivedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.ClaimAsync(Session(f.Installation, f.Runtime), default));
    }

    [Fact]
    public async Task ChiefTrackingCardExpiryBlocksWithoutCreatingAnotherPersonalCallback()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var f = await ChiefFixtureAsync(db, clock.GetUtcNow());
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        await inbox.EnqueueAsync(f.Installation.BusinessId, f.Installation.Id, AgentWorkKind.Capability,
            "assistant.converse.v1", Json("{}"), "audit-expiry", clock.GetUtcNow().AddHours(1));
        Assert.NotNull(await inbox.ClaimAsync(Session(f.Installation, f.Runtime), default));
        clock.Advance(TimeSpan.FromMinutes(6));
        await new WorkItemMutationEngine(db, clock).ReconcileAsync();
        var card = await db.CoreWorkTasks.SingleAsync();
        Assert.Equal(WorkTaskStatus.Blocked, card.Status);
        Assert.Null(card.ClaimEventId);
        Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());
    }

    [Fact]
    public async Task ChiefFailureWithoutAnErrorMessageStillLeavesABlockedCard()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var f = await ChiefFixtureAsync(db, clock.GetUtcNow());
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var work = await inbox.EnqueueAsync(f.Installation.BusinessId, f.Installation.Id, AgentWorkKind.Capability,
            "assistant.converse.v1", Json("{}"), "empty-failure", clock.GetUtcNow().AddHours(1));
        var session = Session(f.Installation, f.Runtime);
        var claim = (await inbox.ClaimAsync(session, default))!;
        await inbox.CompleteAsync(session, work.Id, claim.Attempt, claim.LeaseToken, new(false, null, null), default);
        Assert.Equal(WorkTaskStatus.Blocked, (await db.CoreWorkTasks.SingleAsync()).Status);
    }
}
