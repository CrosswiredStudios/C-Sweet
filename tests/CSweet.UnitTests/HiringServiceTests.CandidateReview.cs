using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Fact]
    public async Task CandidateReview_RevalidatesTheChoiceAndSubmitsTheSelectedCandidateOnlyOnce()
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, requireConfiguration: false, candidates: c =>
            [c, c with { AgentReference = "catalog:specialist", Name = "Specialist", Score = 0.1m }]);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire approved roles.");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var pending = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("AwaitingSelection", pending.Status);
        Assert.Empty(db.StaffingActionProposals);
        Assert.Single(db.AgentPlatformEventOutbox, x => x.EventType == HiringCandidateReviewWake.EventType);
        Task<HiringCandidateSelectionResponse> Read(HiringCandidateDecision? decision = null) =>
            f.Service.ReviewCandidateAsync(f.Organization, f.Installation, new(plan.Id) { Decision = decision });
        var initial = (await Read()).Brief!;
        Assert.Equal(2, initial.Candidates.Count);
        var choice = new HiringCandidateDecision(initial.Evidence, "catalog:specialist", "The specialty fits the project.");
        plan.Objective = "Own the changed project deliverable.";
        await db.SaveChangesAsync();
        var changed = await Read(choice);
        Assert.Null(changed.Candidate);
        Assert.NotEqual(initial.Evidence, changed.Brief!.Evidence);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(choice with {
            Evidence = changed.Brief.Evidence, CandidateReference = "nonexistent" }));
        var selected = await Read(choice with { Evidence = changed.Brief.Evidence });
        Assert.Equal(choice.CandidateReference, selected.Candidate!.AgentReference);
        db.ChangeTracker.Clear();
        Assert.Equal(choice.Rationale, (await Read()).Rationale);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.True(result.Status == "Succeeded", result.Message);
        Assert.Equal("Succeeded", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        Assert.Single(await db.HiringRecommendationFulfillments.ToListAsync());
        Assert.Equal("Specialist", (await db.WorkforceCandidates.SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task CandidateReview_CorrelatesAnswerToPlanAuthorAndWakesTheChiefAtomically()
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, candidates: c => [c, c with { AgentReference = "catalog:second" }]);
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var source = await db.ResourceChangeRequests.SingleAsync();
        source.RequesterOrganizationUserId = Guid.NewGuid();
        source.RequesterInstallationId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser { Id = source.RequesterOrganizationUserId,
            OrganizationId = f.Organization, EmployeeType = EmployeeType.Agent, AgentInstallationId = source.RequesterInstallationId });
        await db.SaveChangesAsync();
        Task<HiringCandidateSelectionResponse> Read(HiringCandidateDecision? decision = null) =>
            f.Service.ReviewCandidateAsync(f.Organization, f.Installation, new(plan.Id) { Decision = decision });
        var initial = (await Read()).Brief!;
        Assert.Equal(source.RequesterOrganizationUserId, initial.PlanAuthorId);
        var ask = new HiringCandidateDecision(initial.Evidence, initial.Candidates[0].AgentReference, "The scope favors the first.",
            "Which surface is in the first release?", initial.Candidates[1].AgentReference, "The second leads on offline delivery.");
        await Assert.ThrowsAsync<ArgumentException>(() => Read(ask with { AlternativeReference = ask.CandidateReference }));
        var asked = (await Read(ask)).Brief!;
        Assert.Equal(asked.DispatchKey, (await Read()).Brief!.DispatchKey);
        var chief = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.Installation);
        var chat = Guid.NewGuid();
        var question = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat, SenderOrganizationUserId = chief.Id,
            IdempotencyKey = $"communication-message:{chief.Id:N}:{asked.DispatchKey}", Content = asked.Question! };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Organization, ConversationId = chat,
            UserMessageId = question.Id, TargetAgentOrganizationUserId = f.Owner, Status = ChatTurnStatus.Running };
        db.CoreConversationMessages.Add(question);
        db.ChatTurns.Add(turn);
        await db.SaveChangesAsync();
        Assert.NotNull((await Read()).Brief!.DispatchKey); // An unrelated recipient cannot satisfy this question.
        turn.TargetAgentOrganizationUserId = source.RequesterOrganizationUserId;
        var reply = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat,
            SenderOrganizationUserId = source.RequesterOrganizationUserId, Content = "Offline delivery first." };
        db.CoreConversationMessages.Add(reply);
        await db.SaveChangesAsync();
        Assert.Null((await Read()).Brief!.DispatchKey);
        var turns = new ChatTurnService(db);
        await turns.CompleteAsync(turn.Id, reply.Id, false);
        await turns.CompleteAsync(turn.Id, reply.Id, false);
        db.ChangeTracker.Clear();
        var answered = (await Read()).Brief!;
        Assert.Equal(reply.Content, answered.Answer);
        Assert.Equal(f.Installation, (await db.AgentPlatformEventOutbox.SingleAsync(x =>
            x.EventType == HiringCandidateReviewWake.EventType && x.IdempotencyKey.EndsWith(turn.Id.ToString("N")))).TargetInstallationId);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(ask with { Evidence = answered.Evidence }));
        var result = await Read(new(answered.Evidence, ask.AlternativeReference!, "The author confirmed offline delivery."));
        Assert.Equal(ask.AlternativeReference, result.Candidate!.AgentReference);
        Assert.Empty(db.StaffingActionProposals); // Owner chose recommendations, not automatic hiring.
    }
}
