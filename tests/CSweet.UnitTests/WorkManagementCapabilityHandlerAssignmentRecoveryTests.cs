using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_initial_stage_does_not_hide_missing_or_self_review_staffing_in_preflight(bool selfReview)
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var owner = db.CoreOrganizationUsers.Local.Single().Id;
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, ManagerOrganizationUserId = owner, Name = "Game" };
        var policy = new WorkOrchestrationPolicy { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id };
        var revision = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), PolicyId = policy.Id, OrganizationId = setup.OrganizationId,
            BoardId = board.Id, InitialStageKey = "ready", IsPublished = true };
        foreach (var key in new[] { "ready", "development", "quality", "done" })
            revision.Stages.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = revision.Id, Key = key, Name = key,
                Type = key == "ready" ? WorkOrchestrationStageType.Queue : key == "done" ? WorkOrchestrationStageType.Terminal : WorkOrchestrationStageType.AgentExecution,
                IsSuccessfulTerminal = key == "done" });
        foreach (var edge in new[] { ("ready", "development"), ("development", "quality"), ("quality", "done") })
            revision.Transitions.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = revision.Id, FromStageKey = edge.Item1, ToStageKey = edge.Item2, OutcomeCode = "ready" });
        policy.PublishedRevisionId = revision.Id; policy.Revisions.Add(revision); board.OrchestrationPolicies.Add(policy);
        db.WorkBoards.Add(board);
        var sprint = new WorkSprint { Id = Guid.NewGuid(), BoardId = board.Id, OrganizationId = setup.OrganizationId, Name = "Sprint", Status = WorkSprintStatus.Planned };
        db.WorkSprints.Add(sprint);
        var item = new WorkTask { Id = Guid.NewGuid(), BoardId = board.Id, SprintId = sprint.Id, OrganizationId = setup.OrganizationId,
            AccountableOrganizationUserId = owner, Status = WorkTaskStatus.Ready, DeliverySpecificationJson = "{}" };
        if (selfReview)
            foreach (var key in new[] { "development", "quality" })
                item.StageAssignments.Add(new() { Id = Guid.NewGuid(), WorkItemId = item.Id, BoardId = board.Id,
                    OrganizationId = setup.OrganizationId, StageKey = key, OrganizationUserId = owner,
                    AgentInstallationId = setup.InstallationId, PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation });
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var result = await new CSweet.Infrastructure.WorkManagement.WorkOrchestrationService(db, TimeProvider.System)
            .PreflightAsync(setup.OrganizationId, board.Id, sprint.Id, setup.InstallationId);
        Assert.False(result.IsValid);
        var code = selfReview ? "assignment.self_review" : "item.assignment_missing";
        Assert.Equal(selfReview ? new[] { "quality" } : ["development", "quality"], result.Errors.Where(e => e.Code == code).Select(e => e.StageKey).Order());
        Assert.All(result.Errors.Where(e => e.Code == code), e => Assert.Equal(item.Id, e.ItemId));
        Assert.Empty(await db.WorkSprintExecutions.ToListAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("future")]
    [InlineData("other-blocker")]
    [InlineData("running")]
    [InlineData("completed")]
    [InlineData("other-manager")]
    [InlineData("changed-delivery")]
    [InlineData("stale")]
    [InlineData("self-review")]
    public async Task Active_assignment_recovery_preserves_work_and_checks_authority(string scenario)
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var owner = db.CoreOrganizationUsers.Local.Single().Id;
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            ManagerOrganizationUserId = scenario == "other-manager" ? Guid.NewGuid() : owner, Name = "Game", WorkstreamId = Guid.NewGuid() };
        var policy = new WorkOrchestrationPolicy { Id = Guid.NewGuid(), BoardId = board.Id, OrganizationId = setup.OrganizationId };
        var revision = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), PolicyId = policy.Id, BoardId = board.Id,
            OrganizationId = setup.OrganizationId, InitialStageKey = "ready", IsPublished = true };
        foreach (var key in new[] { "ready", "development", "technical-review" })
            revision.Stages.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = revision.Id, Key = key,
                Type = key == "ready" ? WorkOrchestrationStageType.Queue : WorkOrchestrationStageType.AgentExecution });
        policy.PublishedRevisionId = revision.Id; policy.Revisions.Add(revision); board.OrchestrationPolicies.Add(policy);
        db.WorkBoards.Add(board);
        var delivery = new Wire.WorkItemDeliverySpecification(Guid.NewGuid(), ["Deliver"], ["Test"]) { BaseBranch = "main" };
        var dev = new Wire.WorkStageAssignment("development", "AgentInstallation", owner, setup.InstallationId);
        var reviewerInstallation = Guid.NewGuid();
        var reviewerEmployee = Guid.NewGuid();
        db.AgentInstallations.Add(new AgentInstallation { Id = reviewerInstallation, InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(), BusinessId = setup.OrganizationId.ToString("D"),
            IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active });
        db.CoreOrganizationUsers.Add(new OrganizationUser { Id = reviewerEmployee, OrganizationId = setup.OrganizationId,
            AgentInstallationId = reviewerInstallation, EmployeeType = EmployeeType.Agent, IsActive = true });
        var oldReviewer = Guid.NewGuid();
        var item = new WorkTask { Id = Guid.NewGuid(), BoardId = board.Id, BoardColumnId = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            AccountableOrganizationUserId = owner, Revision = 4, AssignmentRevision = 7, Status = WorkTaskStatus.Blocked,
            BlockReason = "staffing.assignment_missing", PlanningSpecificationJson = JsonSerializer.Serialize(new Wire.WorkItemPlanningSpecification(["Deliver"], ["Test"], []), JsonOptions),
            DeliverySpecificationJson = JsonSerializer.Serialize(delivery, JsonOptions) };
        item.StageAssignments.Add(new() { Id = Guid.NewGuid(), WorkItemId = item.Id, OrganizationId = setup.OrganizationId,
            BoardId = board.Id, StageKey = "development", PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            OrganizationUserId = owner, AgentInstallationId = setup.InstallationId });
        db.CoreWorkTasks.Add(item);
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            BoardId = board.Id, PolicyRevisionId = revision.Id, Status = WorkSprintExecutionStatus.Active, Revision = 3 };
        var current = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = execution.Id, WorkItemId = item.Id,
            WorkItem = item, CurrentStageKey = "technical-review", Status = WorkItemExecutionStatus.Blocked };
        execution.Items.Add(current);
        var completed = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = current.Id, StageKey = "development",
            Status = WorkStageExecutionStatus.Completed, LastOutcomeCode = "code-published", LastSummary = "Published commit abc" };
        completed.Attempts.Add(new() { Id = Guid.NewGuid(), StageExecutionId = completed.Id, Status = WorkExecutionAttemptStatus.Completed, ResultJson = "{\"commit\":\"abc\"}" });
        current.Stages.Add(completed);
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = current.Id, StageKey = "technical-review",
            StageType = WorkOrchestrationStageType.AgentExecution, Status = WorkStageExecutionStatus.Blocked,
            LastError = scenario == "other-blocker" ? "project.permission_denied" : "staffing.assignment_missing", PrincipalKind = WorkOrchestrationPrincipalKind.Unassigned };
        current.Stages.Add(stage);
        var snapshots = new List<object> { new { workItemId = item.Id, stageKey = "development", principalKind = 2,
            organizationUserId = owner, agentInstallationId = setup.InstallationId } };
        if (scenario is "future" or "running" or "completed")
        {
            snapshots.Add(new { workItemId = item.Id, stageKey = "technical-review", principalKind = 2,
                organizationUserId = owner, agentInstallationId = oldReviewer });
            stage.AgentInstallationId = oldReviewer; stage.PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation;
            stage.Status = scenario == "future" ? WorkStageExecutionStatus.Pending : scenario == "running" ? WorkStageExecutionStatus.Running : WorkStageExecutionStatus.Completed;
        }
        execution.AssignmentSnapshotJson = JsonSerializer.Serialize(snapshots, JsonOptions);
        db.WorkSprintExecutions.Add(execution);
        Grant(db, setup, WorkItemActions.FinalizeDelivery, GrantScopeKind.Board, board.Id);
        await db.SaveChangesAsync();
        var request = new Wire.FinalizeWorkItemDeliveryRequest(board.Id, item.Id,
            scenario == "changed-delivery" ? delivery with { BaseBranch = "other" } : delivery, owner,
            [dev, new("technical-review", "AgentInstallation", scenario == "self-review" ? owner : reviewerEmployee,
                scenario == "self-review" ? setup.InstallationId : reviewerInstallation)], scenario == "stale" ? 1 : 4, "repair-1");
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(setup, WorkItemActions.FinalizeDelivery);
        var result = await InvokeAsync(handler, session, WorkItemActions.FinalizeDelivery, request);
        if (scenario is not ("missing" or "future" or "other-blocker"))
        {
            Assert.False(result.Succeeded);
            Assert.Empty(await db.WorkOrchestrationEvents.ToListAsync());
            return;
        }
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(scenario == "other-blocker" ? WorkStageExecutionStatus.Blocked : WorkStageExecutionStatus.Pending, stage.Status);
        if (scenario == "other-blocker") Assert.Equal("project.permission_denied", stage.LastError);
        Assert.Equal(reviewerInstallation, stage.AgentInstallationId);
        Assert.Equal(7, item.AssignmentRevision); // Published source identity survives staffing repair.
        Assert.Equal("Published commit abc", completed.LastSummary);
        Assert.Single(completed.Attempts);
        Assert.Single(await db.WorkOrchestrationEvents.ToListAsync());
        Assert.Single(await db.AgentPlatformEventOutbox.ToListAsync());
        var replay = await InvokeAsync(handler, session, WorkItemActions.FinalizeDelivery, request);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Single(await db.WorkOrchestrationEvents.ToListAsync());
        Assert.Single(await db.AgentPlatformEventOutbox.ToListAsync());
    }

    [Theory]
    [InlineData("development", null)]
    [InlineData("specialist-execution", "game-engineer")]
    [InlineData("specialist-execution", "software-developer")]
    [InlineData("specialist-execution", "game-quality-assurance")]
    public void Independence_applies_to_all_deliverable_authors_and_matches_employee_across_installations(
        string initialStage, string? role)
    {
        var author = Guid.NewGuid();
        var other = Guid.NewGuid();
        var result = WorkAssignmentIndependence.FindSelfReviewStages([
            (initialStage, Guid.NewGuid(), author, role),
            ("technical-review", Guid.NewGuid(), author, null),
            ("quality", Guid.NewGuid(), other, null)]);
        Assert.Equal(new[] { "technical-review" }, result);
    }
}
