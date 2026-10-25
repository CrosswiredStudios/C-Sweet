using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class UserActionServiceTests
{
    [Fact]
    public async Task ChiefNextFocusChoicesShrinkAndOnlyAnOwnerChoiceActivatesTheNextArea()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var covered = new HashSet<string>();
        foreach (var domain in new[] { "product", "financial", "research", "legal" })
        {
            Assert.Equal(domain, state.Focus!.DomainId);
            var row = state.Assessments.Single(x => x.Id == state.NextAssessmentId);
            await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint,
                new("NotApplicable", "Unresolved", null, "No additional leadership is required for this selected area at the current stage.", "None", []), "complete:" + domain));
            covered.Add(domain);
            db.ChangeTracker.Clear();
            state = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
            Assert.Null(state.NextAssessmentId);
            Assert.DoesNotContain(state.Assessments, x => x.ExecutionStatus == "Ready");
            var count = await db.ExecutiveDecisions.CountAsync();
            var wakes = await db.AgentPlatformEventOutbox.CountAsync();
            await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
            Assert.Equal(count, await db.ExecutiveDecisions.CountAsync());
            Assert.Equal(wakes, await db.AgentPlatformEventOutbox.CountAsync());
            var pending = await db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Pending).ToListAsync();
            if (covered.Count == 4) { Assert.Empty(pending); break; }
            var next = Assert.Single(pending);
            Assert.Equal("What would you like to work on next?", next.Prompt);
            using var options = JsonDocument.Parse(next.OptionsJson);
            var ids = options.RootElement.GetProperty("options").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(5 - covered.Count, ids.Length);
            Assert.Contains("not-now", ids);
            Assert.DoesNotContain(ids, covered.Contains);
            Assert.Empty(await db.WorkforcePlans.ToListAsync());
            var pick = new[] { "financial", "research", "legal" }.First(x => !covered.Contains(x));
            var answer = await AnswerLeadershipAsync(db, f, next.Id, pick);
            await new ChatTurnService(db).CompleteAsync(answer, f.LaterMessage, false);
            // A missed callback must recover the exact native answer after restart.
            db.ChangeTracker.Clear();
            state = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
            Assert.Equal(pick, Assert.Single(state.Assessments, x => x.ExecutionStatus == "Ready").DomainId);
        }
        Assert.Equal(4, await db.ChiefFocusRecords.CountAsync());
        Assert.Equal(3, await db.ChiefFocusPrompts.CountAsync(x => x.Purpose == "NextFocus"));
    }

    [Fact]
    public async Task ChiefNextFocusNothingElsePausesWithoutReopeningCoveredArea()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var row = state.Assessments.Single(x => x.Id == state.NextAssessmentId);
        await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint,
            new("NotApplicable", "Unresolved", null, "No additional product leadership is needed at this stage.", "None", []), "complete"));
        var next = await db.ExecutiveDecisions.SingleAsync(x => x.Status == ExecutiveDecisionStatus.Pending);
        var answer = await AnswerLeadershipAsync(db, f, next.Id, "not-now");
        await new ChatTurnService(db).CompleteAsync(answer, f.LaterMessage, false);
        db.ChangeTracker.Clear();
        var paused = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.True(paused.Paused); Assert.Null(paused.NextAssessmentId);
        Assert.Equal("product", paused.Focus!.DomainId);
        Assert.Equal(next.Id, paused.Focus.SourceDecisionId);
        Assert.Equal("Complete", paused.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        Assert.All(paused.Assessments.Where(x => x.Id != row.Id), x => Assert.Equal("Backlog", x.ExecutionStatus));
        var revision = paused.FocusRevision;
        await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal(revision, (await db.ChiefLeadershipAgendas.SingleAsync()).FocusRevision);
        Assert.DoesNotContain(await db.ExecutiveDecisions.ToListAsync(), x => x.Status == ExecutiveDecisionStatus.Pending);
        var resume = await OwnerLeadershipTurnAsync(db, f, "Resume proactive leadership");
        await Leadership(db).ControlAsync(f.Organization, f.Installation, new(resume, false, revision, "resume"));
        var resumed = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Null(resumed.NextAssessmentId);
        Assert.Single(await db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Pending).ToListAsync());
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefRecoversUntouchedAutomaticResearchSchedulingAndFencesItsOldCommand()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var product = await db.ChiefCoverageRecords.SingleAsync(x => x.DomainId == "product");
        product.ExecutionStatus = "Complete"; product.CoverageStatus = "NotApplicable";
        var research = await db.ChiefCoverageRecords.SingleAsync(x => x.DomainId == "research");
        research.ExecutionStatus = "Ready";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        // Even a stale command submitted before reconnect discovery cannot execute
        // a domain for which the owner never selected a focus.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(research.Id, research.Revision, state.FocusRevision, state.InputFingerprint,
                new("NeedsCoverage", "Unresolved", null, "Unselected research gap.", "Hire", [], RoleKey: "research-lead", RoleTitle: "Research Lead", RoleObjective: "Own research."), "before-recovery")));
        var recovered = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Null(recovered.NextAssessmentId);
        Assert.Equal("Backlog", recovered.Assessments.Single(x => x.Id == research.Id).ExecutionStatus);
        Assert.Single(await db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Pending).ToListAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(research.Id, research.Revision, state.FocusRevision, recovered.InputFingerprint,
                new("NeedsCoverage", "Unresolved", null, "Unselected research gap.", "Hire", [], RoleKey: "research-lead", RoleTitle: "Research Lead", RoleObjective: "Own research."), "old-auto-hire")));
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [HiringSuggestionPostgresFact]
    public async Task ChiefNextFocusCompletionCardMessageAndWakeRollBackTogether()
    {
        var connection = TestConnection();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await db.Database.EnsureCreatedAsync(); var f = await LeadershipFixtureAsync(db);
            var state = await SelectedLeadershipAsync(db, f);
            var row = state.Assessments.Single(x => x.Id == state.NextAssessmentId);
            var messageCount = await db.CoreConversationMessages.CountAsync();
            var wakeCount = await db.AgentPlatformEventOutbox.CountAsync();
            var command = new RecordChiefAssessmentRequest(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint,
                new("NotApplicable", "Unresolved", null, "Product needs no new leadership.", "None", []), "complete-atomic");
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Leadership(db).AssessAsync(f.Organization, f.Installation, command);
                Assert.Single(await db.ChiefFocusPrompts.Where(x => x.Purpose == "NextFocus").ToListAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Equal("Ready", (await db.ChiefCoverageRecords.SingleAsync(x => x.Id == row.Id)).ExecutionStatus);
            Assert.Empty(await db.ChiefFocusPrompts.Where(x => x.Purpose == "NextFocus").ToListAsync());
            Assert.Equal(messageCount, await db.CoreConversationMessages.CountAsync());
            Assert.Equal(wakeCount, await db.AgentPlatformEventOutbox.CountAsync());
            await Leadership(db).AssessAsync(f.Organization, f.Installation, command);
            db.ChangeTracker.Clear();
            await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
            Assert.Single(await db.ChiefFocusPrompts.Where(x => x.Purpose == "NextFocus").ToListAsync());
            Assert.Equal(messageCount + 1, await db.CoreConversationMessages.CountAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [Fact]
    public async Task ChiefExplicitNewPriorityCancelsPendingNextFocusWithoutChangingCompletedCoverage()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var row = state.Assessments.Single(x => x.Id == state.NextAssessmentId);
        await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint,
            new("NotApplicable", "Unresolved", null, "Product needs no new leadership.", "None", []), "complete"));
        var next = await db.ExecutiveDecisions.SingleAsync(x => x.Status == ExecutiveDecisionStatus.Pending);
        var turn = await OwnerLeadershipTurnAsync(db, f, "Focus on financial");
        await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(turn, state.FocusRevision, "explicit", ExplicitDomainId: "financial"));
        db.ChangeTracker.Clear();
        var current = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("financial", current.Assessments.Single(x => x.Id == current.NextAssessmentId).DomainId);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, (await db.ExecutiveDecisions.SingleAsync(x => x.Id == next.Id)).Status);
        Assert.Equal("Complete", current.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        var stale = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation, next.Id, f.Owner, new("research", null, "stale"));
        Assert.False(stale.Succeeded);
    }
}
