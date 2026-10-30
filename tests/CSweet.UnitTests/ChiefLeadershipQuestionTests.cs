using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class UserActionServiceTests
{
    [Theory]
    [InlineData("confirm", "AdequatelyCovered", "Complete")]
    [InlineData("reassign", "AtRisk", "Blocked")]
    public async Task ChiefNamedManagerDecisionBindsTheEmployeeAndRecoversOnce(string choice, string status, string execution)
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Hire); manager.DisplayName = "Marcus Vale";
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
            PositionKey = "product-manager", StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, state.FocusRevision,
            state.InputFingerprint, new("AtRisk", "Agent", f.Hire, "Two delivery commitments need an ownership decision.", "Question", [f.Hire],
                Question: "Should Marcus remain accountable while we resolve the conflicting commitments?"), "capacity-question"));
        var card = await db.ExecutiveDecisions.SingleAsync(x => x.Id == waiting.DecisionId);
        Assert.Contains("Two delivery commitments", card.Prompt);
        Assert.Contains("Who should be accountable for Product", card.Prompt);
        using var options = JsonDocument.Parse(card.OptionsJson);
        var confirm = $"confirm-owner:{f.Hire:N}";
        var first = options.RootElement.GetProperty("options")[0];
        Assert.Equal(confirm, first.GetProperty("id").GetString());
        Assert.Equal("Yes, Marcus Vale is accountable", first.GetProperty("label").GetString());
        var answer = await AnswerLeadershipAsync(db, f, card.Id, choice == "confirm" ? confirm : "reassign");
        db.ChangeTracker.Clear(); // Recover the authenticated answer after a lost callback.
        var recovered = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        var final = recovered.Assessments.Single(x => x.Id == row.Id);
        Assert.Equal(status, final.CoverageStatus); Assert.Equal(execution, final.ExecutionStatus);
        Assert.Equal(f.Hire, final.AccountableOwnerId); Assert.NotEqual(f.Owner, final.AccountableOwnerId);
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
        var again = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal(final.Revision, again.Assessments.Single(x => x.Id == row.Id).Revision);
        Assert.Equal(answer, (await db.ExecutiveDecisions.SingleAsync(x => x.Id == card.Id)).NextChatTurnId);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("ended")]
    [InlineData("future")]
    public async Task ChiefConfirmationCannotRestoreAnUnavailableManager(string change)
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var assignment = new LeadershipAssignment { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
            PositionKey = "product-manager", StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        db.LeadershipAssignments.Add(assignment); await db.SaveChangesAsync();
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, 1, state.InputFingerprint,
            new("AtRisk", "Unresolved", null, "Confirm the accountable product manager.", "Question", [], Question: "Who should remain accountable?"), "ask"));
        if (change == "inactive") (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Hire)).IsActive = false;
        if (change == "ended") assignment.EndsAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        if (change == "future") assignment.StartsAt = DateTimeOffset.UtcNow.AddDays(1);
        await db.SaveChangesAsync();
        var answer = await AnswerLeadershipAsync(db, f, waiting.DecisionId, $"confirm-owner:{f.Hire:N}");
        var captured = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(answer, 1, "confirm", waiting.DecisionId));
        var final = captured.Assessments.Single(x => x.Id == row.Id);
        Assert.Equal("Blocked", final.ExecutionStatus); Assert.Null(final.AccountableOwnerId);
        Assert.Equal("leadership.evidence_missing", final.BlockCode); Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefHumanRequiredDecisionDoesNotOfferUnqualifiedOwnerOrAgentCoverage()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, 1, state.InputFingerprint,
            new("NeedsCoverage", "Unresolved", null, "Licensed professional coverage is required.", "Question", [],
                Question: "How should we arrange licensed coverage?", HumanRequired: true), "licensed"));
        var card = await db.ExecutiveDecisions.SingleAsync(x => x.Id == waiting.DecisionId);
        using var options = JsonDocument.Parse(card.OptionsJson);
        Assert.Equal(new[] { "external", "defer" }, options.RootElement.GetProperty("options").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public async Task ChiefLegacyPendingManagerQuestionIsSupersededWithoutChangingItsHistoryOrAssumingAnAnswer()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Hire); manager.DisplayName = "Marcus Vale";
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
            PositionKey = "product-manager", StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, 1, state.InputFingerprint,
            new("AtRisk", "Agent", f.Hire, "Capacity needs clarification.", "Question", [f.Hire], Question: "Is Marcus accountable?"), "old"));
        var old = await db.ExecutiveDecisions.SingleAsync(x => x.Id == waiting.DecisionId);
        old.OptionsJson = "{\"options\":[{\"id\":\"owner\",\"label\":\"I will own this\"},{\"id\":\"external\",\"label\":\"Use an external professional\"},{\"id\":\"defer\",\"label\":\"Review later\"}]}";
        old.RecommendedOptionId = "owner"; await db.SaveChangesAsync();
        var oldJson = old.OptionsJson; db.ChangeTracker.Clear();
        var read = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        var replacementId = read.Assessments.Single(x => x.Id == row.Id).DecisionId;
        Assert.NotEqual(old.Id, replacementId);
        var history = await db.ExecutiveDecisions.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(ExecutiveDecisionStatus.Superseded, history.Status); Assert.Equal(oldJson, history.OptionsJson);
        Assert.Equal(replacementId, history.SupersededByDecisionId);
        Assert.Equal("WaitingDecision", read.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        var again = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal(replacementId, again.Assessments.Single(x => x.Id == row.Id).DecisionId);
        Assert.Single(await db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Pending).ToListAsync());
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }
}
