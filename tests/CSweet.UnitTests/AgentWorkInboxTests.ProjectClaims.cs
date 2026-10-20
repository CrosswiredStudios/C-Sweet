using System.Text.Json;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkInboxTests
{
    [Theory]
    [InlineData("active", false)]
    [InlineData("active", true)]
    [InlineData("deadline", false)]
    [InlineData("expired", false)]
    [InlineData("expired", true)]
    [InlineData("blocked", true)]
    [InlineData("other-event", false)]
    [InlineData("reassigned", true)]
    [InlineData("revoked-board-grant", false)]
    [InlineData("revoked-capability", true)]
    [InlineData("removed-team-member", false)]
    [InlineData("removed-participant", true)]
    [InlineData("disabled", false)]
    [InlineData("archived-board", true)]
    [InlineData("wrong-binding", true)]
    [InlineData("wrong-context", true)]
    [InlineData("reassigned-employee", true)]
    [InlineData("archived-employee", true)]
    [InlineData("waiting", true)]
    [InlineData("scheduled", false)]
    public async Task RuntimeRenewalKeepsOnlyItsAuthorizedLiveProjectClaimAlive(string scenario, bool boundContext)
    {
        await using var db = CreateDb(); var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var org = Guid.NewGuid(); var installation = Installation(clock.GetUtcNow());
        installation.BusinessId = org.ToString("D");
        installation.Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { CSweet.Contracts.WorkManagement.PersonalTodoActions.Claim }) };
        var runtime = Runtime(installation, clock.GetUtcNow());
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = installation.Id,
            EmployeeType = EmployeeType.Agent, IsActive = true };
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = org, Name = "Project team", LeadOrganizationUserId = owner.Id };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Status = WorkstreamStatus.Active,
            AccountableManagerOrganizationUserId = owner.Id };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, Kind = WorkBoardKind.Standard,
            WorkstreamId = project.Id, TeamId = team.Id };
        var eventId = Guid.NewGuid();
        var task = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Board = board, Revision = 17,
            AssignedEmployeeId = owner.Id, AssignedAgentInstallationId = installation.Id, Status = WorkTaskStatus.Running,
            ClaimEventId = eventId, ClaimExpiresAt = clock.GetUtcNow().AddMinutes(5),
            SourceConversationId = Guid.NewGuid(), SourceMessageId = Guid.NewGuid() };
        var member = new TeamMembership { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, OrganizationUserId = owner.Id };
        var participant = new ProjectParticipant { OrganizationId = org, WorkstreamId = project.Id, OrganizationUserId = owner.Id,
            AddedByOrganizationUserId = owner.Id, JoinedAt = clock.GetUtcNow() };
        var boardGrant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = org,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installation.Id, ScopeKind = GrantScopeKind.Board,
            ScopeId = board.Id, Action = CSweet.Contracts.WorkManagement.WorkItemActions.Read, GrantedAt = clock.GetUtcNow() };
        db.AddRange(installation, runtime, owner, team, project, board, task, member, participant, boardGrant);
        db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org,
            WorkstreamId = project.Id, TeamId = team.Id, StartsAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), clock);
        var sourceId = boundContext || scenario == "other-event" ? Guid.NewGuid() : eventId;
        var work = await inbox.EnqueueAsync(installation.BusinessId, installation.Id, AgentWorkKind.Event,
            CSweet.WorkManagement.Contracts.PersonalTodoEvents.Available, Json("{}"), "project-renewal",
            clock.GetUtcNow().AddMinutes(scenario == "deadline" ? 7 : 60), sourceId: sourceId.ToString("D"));
        var session = Session(installation, runtime); var claim = (await inbox.ClaimAsync(session, default))!;
        var attempt = await db.AgentWorkAttempts.SingleAsync();
        if (boundContext)
        {
            var executionContext = await db.WorkExecutionContexts.SingleAsync(x => x.Id == attempt.Id);
            executionContext.OrganizationId = scenario == "wrong-context" ? Guid.NewGuid() : org;
            executionContext.RootWorkItemId = task.Id;
            db.WorkItemActivities.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, WorkItemId = task.Id,
                ActorKind = GrantSubjectKind.AgentInstallation, ActorSubjectId = installation.Id, EventType = "agent.ticket.execution.bound",
                DataJson = JsonSerializer.Serialize(new { agentWorkAttemptId = attempt.Id,
                    claimEventId = scenario == "wrong-binding" ? Guid.NewGuid() : eventId }), OccurredAt = clock.GetUtcNow() });
        }
        if (scenario == "expired") task.ClaimExpiresAt = clock.GetUtcNow().AddSeconds(-1);
        if (scenario == "blocked") task.Status = WorkTaskStatus.Blocked;
        if (scenario == "reassigned") task.AssignedAgentInstallationId = Guid.NewGuid();
        if (scenario == "revoked-board-grant") boardGrant.RevokedAt = clock.GetUtcNow();
        if (scenario == "revoked-capability") installation.Grant.RequiredCapabilitiesJson = "[]";
        if (scenario == "removed-team-member") member.EndedAt = clock.GetUtcNow();
        if (scenario == "removed-participant") participant.RemovedAt = clock.GetUtcNow();
        if (scenario == "disabled") installation.IsEnabled = false;
        if (scenario == "archived-board") board.ArchivedAt = clock.GetUtcNow();
        if (scenario == "reassigned-employee") task.AssignedEmployeeId = Guid.NewGuid();
        if (scenario == "archived-employee") owner.ArchivedAt = clock.GetUtcNow();
        if (scenario == "waiting") task.WaitingReason = "Manager decision required";
        if (scenario == "scheduled") task.NextReviewAt = clock.GetUtcNow().AddHours(1);
        await db.SaveChangesAsync(); var originalExpiry = task.ClaimExpiresAt;
        for (var index = 0; index < 12; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await inbox.RenewAsync(session, work.Id, claim.Attempt, claim.LeaseToken, default);
        }
        await db.Entry(task).ReloadAsync();
        Assert.Equal(17, task.Revision);
        var expected = scenario == "active" ? clock.GetUtcNow().AddMinutes(5) :
            scenario == "deadline" ? work.DeadlineAt : originalExpiry;
        Assert.Equal(expected, task.ClaimExpiresAt);
        await inbox.CancelAsync(work.Id, "Stopped", default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.RenewAsync(session, work.Id, claim.Attempt, claim.LeaseToken, default));
    }
}
