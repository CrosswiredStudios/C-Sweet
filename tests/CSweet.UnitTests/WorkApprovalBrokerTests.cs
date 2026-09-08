using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagerStageDecision_ThroughBrokerPersistsAndReplaysExactly(bool approved)
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var request = await SeedApprovalAsync(db, setup, approved);
        var handler = CreateHandler(db, new TestAuditEventWriter());
        Assert.True(handler.CanHandle(WorkOrchestrationActions.DecideApproval));
        Assert.True(handler.CanHandle(WorkItemActions.FinalizeDelivery));
        var session = Session(setup, WorkOrchestrationActions.DecideApproval);
        var first = await InvokeAsync(handler, session, WorkOrchestrationActions.DecideApproval, request);
        Assert.True(first.Succeeded, first.Error);
        db.ChangeTracker.Clear();
        var replay = await InvokeAsync(handler, session, WorkOrchestrationActions.DecideApproval, request);
        Assert.True(replay.Succeeded, replay.Error);
        db.ChangeTracker.Clear();
        Assert.Equal(approved ? "done" : "implementation", (await db.WorkItemExecutions.SingleAsync()).CurrentStageKey);
        Assert.Single(await db.WorkOrchestrationEvents.ToListAsync());
        Assert.Equal(2, await db.WorkStageExecutions.CountAsync());
        var changed = await InvokeAsync(handler, session, WorkOrchestrationActions.DecideApproval,
            request with { Approved = !approved });
        Assert.False(changed.Succeeded);
        Assert.Single(await db.WorkOrchestrationEvents.ToListAsync());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("package")]
    [InlineData("manager")]
    [InlineData("assignee")]
    [InlineData("sprint")]
    [InlineData("paused")]
    [InlineData("historical")]
    public async Task ManagerStageDecision_DeniesInvalidAuthorityOrExecution(string condition)
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var request = await SeedApprovalAsync(db, setup, true);
        if (condition == "grant") db.ScopedActionGrants.RemoveRange(await db.ScopedActionGrants.ToListAsync());
        if (condition == "manager") (await db.WorkBoards.SingleAsync()).ManagerOrganizationUserId = Guid.NewGuid();
        if (condition == "assignee") (await db.WorkStageExecutions.SingleAsync()).OrganizationUserId = Guid.NewGuid();
        if (condition == "sprint") request = request with { SprintExecutionId = Guid.NewGuid() };
        if (condition == "paused") (await db.WorkSprintExecutions.SingleAsync()).Status = WorkSprintExecutionStatus.Paused;
        if (condition == "historical") (await db.WorkItemExecutions.SingleAsync()).Traversal++;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var result = await InvokeAsync(CreateHandler(db, new TestAuditEventWriter()),
            condition == "package" ? Session(setup) : Session(setup, WorkOrchestrationActions.DecideApproval),
            WorkOrchestrationActions.DecideApproval, request);
        Assert.False(result.Succeeded);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.WorkOrchestrationEvents.ToListAsync());
        Assert.Equal(WorkStageExecutionStatus.WaitingForApproval, (await db.WorkStageExecutions.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("failed-latest", false)]
    [InlineData("wrong-attempt", false)]
    [InlineData("wrong-stage", false)]
    [InlineData("invalid-json", false)]
    [InlineData("running-stage", false)]
    public async Task OrchestrationRead_ExposesOnlyCurrentCompletedAttemptEvidence(string condition, bool expected)
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var request = await SeedApprovalAsync(db, setup, true);
        Grant(db, setup, WorkOrchestrationActions.Read, GrantScopeKind.Board, request.BoardId);
        var stage = await db.WorkStageExecutions.SingleAsync();
        stage.Status = condition == "running-stage" ? WorkStageExecutionStatus.Running : WorkStageExecutionStatus.Completed;
        stage.LastOutcomeCode = "completed";
        var attemptId = Guid.NewGuid();
        var outcome = new Wire.WorkExecutionOutcomeV1(condition == "wrong-stage" ? Guid.NewGuid() : stage.Id,
            condition == "wrong-attempt" ? Guid.NewGuid() : attemptId, "Completed", "completed", "Delivery evidence",
            System.Text.Json.JsonSerializer.SerializeToElement(new { files = new[] { "game/index.html" } }),
            [new("commit", "Candidate", "abc123")], []);
        db.WorkExecutionAttempts.Add(new() { Id = attemptId, StageExecutionId = stage.Id,
            Attempt = 1, Status = WorkExecutionAttemptStatus.Completed,
            ResultJson = condition == "invalid-json" ? "{" : System.Text.Json.JsonSerializer.Serialize(outcome, JsonOptions) });
        if (condition == "failed-latest")
            db.WorkExecutionAttempts.Add(new() { Id = Guid.NewGuid(), StageExecutionId = stage.Id,
                Attempt = 2, Status = WorkExecutionAttemptStatus.Failed });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var result = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.Read),
            WorkOrchestrationActions.Read, new Wire.ReadWorkOrchestrationRequest(request.BoardId,
                SprintExecutionId: request.SprintExecutionId));
        Assert.True(result.Succeeded, result.Error);
        var response = System.Text.Json.JsonSerializer.Deserialize<Wire.WorkSprintExecutionResponse>(result.Payload.ToByteArray(), JsonOptions)!;
        var evidence = Assert.Single(Assert.Single(response.Items).Stages).LatestOutcome;
        Assert.Equal(expected, evidence is not null);
        if (expected)
        {
            Assert.Equal(attemptId, evidence!.AttemptId);
            Assert.Contains("game/index.html", evidence.Output.GetRawText());
            Assert.Equal("abc123", Assert.Single(evidence.Evidence).Value);
        }
        db.ScopedActionGrants.RemoveRange(await db.ScopedActionGrants.Where(x => x.Action == WorkOrchestrationActions.Read).ToListAsync());
        await db.SaveChangesAsync();
        var denied = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.Read),
            WorkOrchestrationActions.Read, new Wire.ReadWorkOrchestrationRequest(request.BoardId,
                SprintExecutionId: request.SprintExecutionId));
        Assert.False(denied.Succeeded);
    }

    private static async Task<Wire.DecideWorkApprovalStageRequest> SeedApprovalAsync(
        CSweet.Infrastructure.Persistence.CSweetDbContext db, Setup setup, bool approved)
    {
        var managerId = db.CoreOrganizationUsers.Local.Single(x => x.AgentInstallationId == setup.InstallationId).Id;
        var boardId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var sprintId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemExecutionId = Guid.NewGuid();
        var stageId = Guid.NewGuid();
        db.WorkBoards.Add(new WorkBoard { Id = boardId, OrganizationId = setup.OrganizationId,
            Name = "Game delivery", ManagerOrganizationUserId = managerId });
        Grant(db, setup, WorkOrchestrationActions.DecideApproval, GrantScopeKind.Board, boardId);
        db.CoreWorkTasks.Add(new WorkTask { Id = itemId, BoardId = boardId, OrganizationId = setup.OrganizationId,
            Title = "Playable phase", Status = WorkTaskStatus.WaitingForApproval });
        db.WorkOrchestrationPolicyRevisions.Add(new WorkOrchestrationPolicyRevision {
            Id = policyId, BoardId = boardId, OrganizationId = setup.OrganizationId,
            Stages = [new() { Id = Guid.NewGuid(), Key = "producer-review", Type = WorkOrchestrationStageType.ManagerApproval },
                new() { Id = Guid.NewGuid(), Key = "done", Type = WorkOrchestrationStageType.Terminal, IsSuccessfulTerminal = true },
                new() { Id = Guid.NewGuid(), Key = "implementation", Type = WorkOrchestrationStageType.Queue }],
            Transitions = [new() { Id = Guid.NewGuid(), FromStageKey = "producer-review", OutcomeCode = "approved", ToStageKey = "done" },
                new() { Id = Guid.NewGuid(), FromStageKey = "producer-review", OutcomeCode = "rejected", ToStageKey = "implementation", MaximumTraversals = 3 }]
        });
        db.WorkSprintExecutions.Add(new WorkSprintExecution { Id = sprintId, BoardId = boardId,
            OrganizationId = setup.OrganizationId, PolicyRevisionId = policyId,
            StartedByOrganizationUserId = managerId, Status = WorkSprintExecutionStatus.Active, AssignmentSnapshotJson = "[]" });
        db.WorkItemExecutions.Add(new WorkItemExecution { Id = itemExecutionId, WorkItemId = itemId,
            SprintExecutionId = sprintId, CurrentStageKey = "producer-review", Status = WorkItemExecutionStatus.WaitingForApproval });
        db.WorkStageExecutions.Add(new WorkStageExecution { Id = stageId, ItemExecutionId = itemExecutionId,
            StageKey = "producer-review", StageType = WorkOrchestrationStageType.ManagerApproval,
            PrincipalKind = WorkOrchestrationPrincipalKind.BoardManager, OrganizationUserId = managerId,
            Status = WorkStageExecutionStatus.WaitingForApproval });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(boardId, sprintId, stageId, approved, "Reviewed delivery against acceptance criteria.", "producer-acceptance");
    }
}
