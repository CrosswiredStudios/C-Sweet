using System.Text.Json;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed class WorkSprintRealtimeTests
{
    [Theory]
    [InlineData(WorkItemActions.Read)]
    [InlineData(WorkSprintActions.Read)]
    public async Task StartCommitsBoardRefreshHintAndReplayDoesNotDuplicateIt(string detailReadAction)
    {
        await using var db = CreateDb();
        var (board, sprint, manager) = await SeedAsync(db, detailReadAction);
        var service = new WorkOrchestrationService(db, TimeProvider.System);
        var request = new WorkOrchestrationControlRequest(sprint.Revision, "start");
        var result = await service.StartAsync(board.OrganizationId, board.Id, sprint.Id, manager.ApplicationUserId!.Value, request);
        await service.StartAsync(board.OrganizationId, board.Id, sprint.Id, manager.ApplicationUserId.Value, request);

        db.ChangeTracker.Clear();
        Assert.Equal("Active", result.Status);
        Assert.Equal(WorkSprintStatus.Active, (await db.WorkSprints.SingleAsync()).Status);
        await AssertHintAsync(db, board, sprint.Id, manager.Id, "sprint.started");
    }

    [Theory]
    [InlineData("pause", WorkSprintExecutionStatus.Active, WorkSprintStatus.Paused)]
    [InlineData("resume", WorkSprintExecutionStatus.Paused, WorkSprintStatus.Active)]
    [InlineData("cancel", WorkSprintExecutionStatus.Active, WorkSprintStatus.Cancelled)]
    public async Task LifecycleControlCommitsRefreshWithoutAnyTicketMutation(string action,
        WorkSprintExecutionStatus initial, WorkSprintStatus expected)
    {
        await using var db = CreateDb();
        var (board, sprint, manager) = await SeedAsync(db);
        sprint.Status = initial == WorkSprintExecutionStatus.Paused ? WorkSprintStatus.Paused : WorkSprintStatus.Active;
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = board.OrganizationId,
            BoardId = board.Id, SprintId = sprint.Id, Status = initial };
        db.WorkSprintExecutions.Add(execution);
        await db.SaveChangesAsync();
        var service = new WorkOrchestrationService(db, TimeProvider.System);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ControlAsync(board.OrganizationId,
            board.Id, sprint.Id, manager.ApplicationUserId!.Value, action, new(execution.Revision - 1, "stale")));
        Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == AppRealtimeEvents.WorkBoardChanged).ToListAsync());
        await service.ControlAsync(board.OrganizationId, board.Id, sprint.Id, manager.ApplicationUserId!.Value,
            action, new(execution.Revision, action));

        db.ChangeTracker.Clear();
        Assert.Equal(expected, (await db.WorkSprints.SingleAsync()).Status);
        await AssertHintAsync(db, board, sprint.Id, manager.Id, $"sprint.execution.{action}");
    }

    [Fact]
    public async Task FailedPreflightDoesNotPublishASprintStart()
    {
        await using var db = CreateDb();
        var (board, sprint, manager) = await SeedAsync(db);
        (await db.CoreWorkTasks.SingleAsync()).DeliverySpecificationJson = null;
        await db.SaveChangesAsync();
        var service = new WorkOrchestrationService(db, TimeProvider.System);

        await Assert.ThrowsAsync<WorkOrchestrationValidationException>(() => service.StartAsync(board.OrganizationId,
            board.Id, sprint.Id, manager.ApplicationUserId!.Value, new(sprint.Revision, "invalid-start")));

        db.ChangeTracker.Clear();
        Assert.Equal(WorkSprintStatus.Planned, (await db.WorkSprints.SingleAsync()).Status);
        Assert.Empty(await db.WorkSprintExecutions.ToListAsync());
        Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == AppRealtimeEvents.WorkBoardChanged).ToListAsync());
    }

    private static async Task AssertHintAsync(CSweetDbContext db, WorkBoard board, Guid sprintId, Guid readerId, string change)
    {
        var hint = Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == AppRealtimeEvents.WorkBoardChanged).ToListAsync());
        Assert.Equal(AppRealtimeEvents.WorkBoardChanged, hint.EventType);
        Assert.Equal(board.OrganizationId, hint.OrganizationId);
        Assert.Equal($"organizations/{board.OrganizationId:D}/work/boards/{board.Id:D}", hint.Subject);
        Assert.Equal([readerId], JsonSerializer.Deserialize<Guid[]>(hint.RecipientOrganizationUserIdsJson!)!);
        using var data = JsonDocument.Parse(hint.DataJson);
        Assert.Equal(board.Id, data.RootElement.GetProperty("boardId").GetGuid());
        Assert.Equal(sprintId, data.RootElement.GetProperty("sprintId").GetGuid());
        Assert.Equal(change, data.RootElement.GetProperty("changeType").GetString());
        Assert.Equal((await db.WorkSprints.SingleAsync()).Revision, data.RootElement.GetProperty("revision").GetInt64());
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static async Task<(WorkBoard, WorkSprint, OrganizationUser)> SeedAsync(CSweetDbContext db,
        string detailReadAction = WorkItemActions.Read)
    {
        var org = Guid.NewGuid();
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Manager", EmployeeType = EmployeeType.Human, IsActive = true };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, ManagerOrganizationUserId = manager.Id, Name = "Delivery" };
        var sprint = new WorkSprint { Id = Guid.NewGuid(), BoardId = board.Id, OrganizationId = org, Name = "Sprint 1" };
        var policy = new WorkOrchestrationPolicy { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id };
        var revision = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), PolicyId = policy.Id,
            OrganizationId = org, BoardId = board.Id, InitialStageKey = "done", IsPublished = true };
        policy.PublishedRevisionId = revision.Id;
        var terminal = new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = revision.Id,
            Key = "done", Type = WorkOrchestrationStageType.Terminal, IsSuccessfulTerminal = true };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, SprintId = sprint.Id,
            Identifier = "DELIVERY-1", Status = WorkTaskStatus.Ready, IsExecutable = true,
            DeliverySpecificationJson = "{}", AccountableOrganizationUserId = manager.Id };
        db.AddRange(manager, board, sprint, policy, revision, terminal, item);
        // Only an active human with board and item/sprint read authority receives the hint.
        foreach (var condition in new[] { "allowed", "missing-item", "expired", "revoked", "agent", "inactive", "other-board", "other-org" })
        {
            var reader = condition == "allowed" ? manager : new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org,
                DisplayName = condition, EmployeeType = condition == "agent" ? EmployeeType.Agent : EmployeeType.Human,
                IsActive = condition != "inactive" };
            if (reader != manager) db.CoreOrganizationUsers.Add(reader);
            foreach (var action in new[] { WorkBoardActions.Read, detailReadAction })
            {
                if (condition == "missing-item" && action == detailReadAction) continue;
                db.ScopedActionGrants.Add(new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = condition == "other-org" ? Guid.NewGuid() : org,
                    SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = reader.Id, Action = action,
                    ScopeKind = GrantScopeKind.Board, ScopeId = condition == "other-board" ? Guid.NewGuid() : board.Id,
                    ExpiresAt = condition == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : null,
                    RevokedAt = condition == "revoked" ? DateTimeOffset.UtcNow : null });
            }
        }
        await db.SaveChangesAsync();
        return (board, sprint, manager);
    }
}
