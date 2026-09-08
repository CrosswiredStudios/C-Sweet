using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Security;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class MergeReviewerAuthorizationTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("technical-review", true)]
    [InlineData("dispatching", true)]
    [InlineData("canonical-lead", true)]
    [InlineData("membership", false)]
    [InlineData("employee", false)]
    [InlineData("archived-team", false)]
    [InlineData("completed-stage", false)]
    [InlineData("other-stage", false)]
    [InlineData("other-installation", false)]
    [InlineData("old-traversal", false)]
    [InlineData("paused-sprint", false)]
    [InlineData("completed-item", false)]
    [InlineData("other-organization", false)]
    [InlineData("revision", false)]
    [InlineData("grant", false)]
    public async Task OnlyCurrentReviewerOrCanonicalLeadWithGrantCanReview(string scenario, bool allowed)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var employee = Guid.NewGuid();
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = org, LeadOrganizationUserId = Guid.NewGuid(), Name = "Game" };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, Name = "Game" };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, AssignmentRevision = 7 };
        var user = new OrganizationUser { Id = employee, OrganizationId = org, AgentInstallationId = installation, IsActive = true, DisplayName = "Technical Director" };
        var membership = new TeamMembership { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, OrganizationUserId = employee };
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Status = WorkSprintExecutionStatus.Active };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = sprint.Id, WorkItemId = item.Id,
            Status = WorkItemExecutionStatus.Running, CurrentStageKey = "merge-decision", Traversal = 2 };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id, StageKey = "merge-decision",
            AgentInstallationId = installation, OrganizationUserId = employee, Status = WorkStageExecutionStatus.Running, Traversal = 2 };
        var auth = new Authorization { Allowed = scenario != "grant" };
        switch (scenario)
        {
            case "technical-review": stage.StageKey = execution.CurrentStageKey = "technical-review"; break;
            case "dispatching": stage.Status = WorkStageExecutionStatus.Dispatching; break;
            case "canonical-lead": team.LeadOrganizationUserId = employee; stage.Status = WorkStageExecutionStatus.Completed; break;
            case "membership": membership.EndedAt = DateTimeOffset.UtcNow; break;
            case "employee": user.IsActive = false; break;
            case "archived-team": team.ArchivedAt = DateTimeOffset.UtcNow; break;
            case "completed-stage": stage.Status = WorkStageExecutionStatus.Completed; break;
            case "other-stage": stage.StageKey = "development"; break;
            case "other-installation": stage.AgentInstallationId = Guid.NewGuid(); break;
            case "old-traversal": stage.Traversal = 1; break;
            case "paused-sprint": sprint.Status = WorkSprintExecutionStatus.Paused; break;
            case "completed-item": execution.Status = WorkItemExecutionStatus.Completed; break;
            case "other-organization": sprint.OrganizationId = Guid.NewGuid(); break;
        }
        db.AddRange(team, board, item, user, membership, sprint, execution, stage);
        await db.SaveChangesAsync();
        var handler = new GitWorkspaceCapabilityHandler(db, new UnavailableTrustedGitHostClient(), auth, null!);
        foreach (var action in new[] { GitMergeCapabilities.Review, GitMergeCapabilities.Authorize })
        {
            var task = handler.RequireMergeReviewerAsync(org, installation, item.Id, scenario == "revision" ? 6 : 7, action, default);
            if (allowed && !(scenario == "technical-review" && action == GitMergeCapabilities.Authorize))
            {
                var reviewer = await task;
                Assert.Equal(employee, reviewer.OrganizationUserId);
                Assert.Equal(team.Id, reviewer.TeamId);
                Assert.Equal((org, installation, action, GrantScopeKind.WorkItem, (Guid?)item.Id), auth.LastRequest);
            }
            else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => task);
        }
    }

    private sealed class Authorization : IScopedActionAuthorizationService
    {
        public bool Allowed { get; set; }
        public (Guid, Guid, string, GrantScopeKind, Guid?) LastRequest { get; private set; }
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid organizationId, GrantSubjectKind subjectKind, Guid subjectId,
            string action, GrantScopeKind resourceScopeKind, Guid? resourceScopeId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(GrantSubjectKind.AgentInstallation, subjectKind);
            LastRequest = (organizationId, subjectId, action, resourceScopeKind, resourceScopeId);
            return Task.FromResult(new ScopedAuthorizationDecision(Allowed, action));
        }
    }
}
