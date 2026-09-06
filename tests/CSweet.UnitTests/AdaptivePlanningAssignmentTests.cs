using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;
using SharedWork = CSweet.WorkManagement.Contracts;
using CSweet.Contracts.WorkManagement;
namespace CSweet.UnitTests;
public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Fact]
    public async Task LateHireBindsExistingPlanningTicketWithRevisionAndReplayChecks()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var accountableUserId = db.CoreOrganizationUsers.Local.Single().Id;
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            Name = "Delivery", Description = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Columns = [Column("Backlog", WorkBoardColumnCategory.ToDo, 0)]
        };
        var policy = new WorkOrchestrationPolicy
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            Name = "Software delivery", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var revision = new WorkOrchestrationPolicyRevision
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            PolicyId = policy.Id, Revision = 1, Name = policy.Name, InitialStageKey = "development",
            IsPublished = true, CreatedAt = DateTimeOffset.UtcNow, PublishedAt = DateTimeOffset.UtcNow
        };
        revision.Stages.Add(new WorkOrchestrationStage
        {
            Id = Guid.NewGuid(), PolicyRevisionId = revision.Id, Key = "development",
            Name = "Development", Type = WorkOrchestrationStageType.AgentExecution
        });
        policy.PublishedRevisionId = revision.Id;
        policy.Revisions.Add(revision);
        board.OrchestrationPolicies.Add(policy);
        db.WorkBoards.Add(board);
        Grant(db, setup, WorkItemActions.Create, GrantScopeKind.Board, board.Id);
        Grant(db, setup, WorkItemActions.RevisePlanning, GrantScopeKind.Board, board.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(setup, WorkItemActions.Create, WorkItemActions.RevisePlanning);

        var parentResult = await InvokeAsync(handler, session, WorkItemActions.Create, new
        {
            boardId = board.Id,
            title = "Flight controls",
            typeKey = SharedWork.WorkItemTypeKeys.GeneralEpicV1,
            kind = "Epic",
            priority = "High",
            idempotencyKey = "planning-epic-1"
        });
        Assert.True(parentResult.Succeeded, parentResult.Error);
        using var parentJson = JsonDocument.Parse(parentResult.Payload.ToByteArray());
        var parentItemId = parentJson.RootElement.GetProperty("id").GetGuid();

        var created = await InvokeAsync(handler, session, WorkItemActions.Create, new
        {
            boardId = board.Id,
            title = "Implement flight controls",
            typeKey = SharedWork.WorkItemTypeKeys.GeneralStoryV1,
            kind = "Story",
            parentItemId,
            priority = "High",
            idempotencyKey = "planning-ticket-1",
            planning = new
            {
                requirements = new[] { "Support pitch and yaw input." },
                acceptanceCriteria = new[] { "Controls respond within one rendered frame." },
                constraints = new[] { "Keep input handling deterministic." }
            }
        });
        Assert.True(created.Succeeded, created.Error);
        using var createdJson = JsonDocument.Parse(created.Payload.ToByteArray());
        var itemId = createdJson.RootElement.GetProperty("id").GetGuid();
        var itemRevision = createdJson.RootElement.GetProperty("revision").GetInt64();
        Assert.Equal(JsonValueKind.Object, createdJson.RootElement.GetProperty("planning").ValueKind);
        Assert.Equal(JsonValueKind.Null, createdJson.RootElement.GetProperty("delivery").ValueKind);


        var request = new SharedWork.ReviseWorkItemPlanningRequest(board.Id, itemId,
            "Implement flight controls", "Deliver controls", parentItemId,
            new SharedWork.WorkItemPlanningSpecification(["Support pitch and yaw input."], ["Controls respond within one rendered frame."], []),
            itemRevision, 1, "bind-late-hire")
        {
            AccountableOrganizationUserId = accountableUserId,
            StageAssignments = [new SharedWork.WorkStageAssignment("development", "AgentInstallation", accountableUserId, setup.InstallationId)]
        };
        var result = await InvokeAsync(handler, session, WorkItemActions.RevisePlanning, request);
        Assert.True(result.Succeeded, result.Error);
        var replay = await InvokeAsync(handler, session, WorkItemActions.RevisePlanning, request);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Single(await db.WorkItemStageAssignments.ToListAsync());
        Assert.Equal(2, await db.CoreWorkTasks.CountAsync());
        var ticket = await db.CoreWorkTasks.SingleAsync(x => x.Id == itemId);
        Assert.Equal(accountableUserId, ticket.AccountableOrganizationUserId);
        Assert.Equal(2, ticket.PlanningRevision);
        var stale = await InvokeAsync(handler, session, WorkItemActions.RevisePlanning, request with { IdempotencyKey = "stale-bind" });
        Assert.False(stale.Succeeded);
        var invalid = await InvokeAsync(handler, session, WorkItemActions.RevisePlanning, request with
        {
            ExpectedRevision = ticket.Revision, ExpectedPlanningRevision = ticket.PlanningRevision,
            IdempotencyKey = "invalid-stage",
            StageAssignments = [new SharedWork.WorkStageAssignment("unknown", "AgentInstallation", accountableUserId, setup.InstallationId)]
        });
        Assert.False(invalid.Succeeded);
        Assert.Single(await db.WorkItemStageAssignments.ToListAsync());
    }
}
