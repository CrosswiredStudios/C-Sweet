using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentTicketFeedbackTests
{
    private const string Error = "agent-failure:v1;code=runtime.transport;exceptionType=IOException;diagnosticId=one";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedFailureBlocksAndEscalatesExactlyOnce(bool agentManager)
    {
        await using var db = CreateDb();
        var (ticket, owner, manager) = Seed(db, agentManager);
        var now = DateTimeOffset.UtcNow;
        Assert.False(await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId!.Value,
            "attempt-1", Error, true, now, default));
        await db.SaveChangesAsync();
        Assert.Equal(WorkTaskStatus.Running, ticket.Status);
        Assert.Empty(db.CoreConversationMessages);
        Assert.True(await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "attempt-2", Error.Replace("diagnosticId=one", "diagnosticId=two"), true, now.AddMinutes(1), default));
        await db.SaveChangesAsync();
        Assert.Equal(WorkTaskStatus.Blocked, ticket.Status);
        Assert.Null(ticket.ClaimEventId);
        Assert.Null(ticket.NextReviewAt);
        Assert.Equal(2, await db.WorkItemComments.CountAsync());
        var notification = Assert.Single(db.UserNotifications);
        Assert.Equal(manager.Id, notification.RecipientOrganizationUserId);
        Assert.Single(db.CoreConversationMessages);
        Assert.Equal(2, await db.ConversationParticipants.CountAsync());
        Assert.Equal(agentManager ? 1 : 0, await db.AgentPlatformEventOutbox.CountAsync());
        Assert.True(await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "attempt-2", Error, true, now.AddMinutes(2), default));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.WorkItemComments.CountAsync());
        Assert.Single(db.CoreConversationMessages);
        Assert.Single(db.UserNotifications);
        Assert.Equal(2, await db.ApplicationRealtimeOutbox.CountAsync(x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged));
        Assert.All(db.WorkItemComments, comment =>
        {
            Assert.True(comment.Body.Length < 400);
            Assert.DoesNotContain("diagnosticId", comment.Body);
        });
    }

    [Fact]
    public async Task PlanFailureCommentsOnRunningTaskAndBlocksItsCoordinator()
    {
        await using var db = CreateDb();
        var (root, owner, _) = Seed(db, false);
        root.Kind = WorkItemKind.Epic;
        var child = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = root.OrganizationId, BoardId = root.BoardId,
            Kind = WorkItemKind.Task, Status = WorkTaskStatus.Running, Title = "Implement rules",
            PlanningSpecificationJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                personalPlan = new { rootItemId = root.Id }
            })
        };
        db.CoreWorkTasks.Add(child);
        await db.SaveChangesAsync();
        await AgentTicketFeedback.RecordFailureAsync(db, root, owner.AgentInstallationId!.Value,
            "one", Error, true, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        Assert.Equal(child.Id, Assert.Single(db.WorkItemComments).WorkItemId);
        Assert.True(await AgentTicketFeedback.RecordFailureAsync(db, root, owner.AgentInstallationId.Value,
            "two", Error, true, DateTimeOffset.UtcNow.AddMinutes(1), default));
        await db.SaveChangesAsync();
        Assert.Equal(WorkTaskStatus.Blocked, child.Status);
        Assert.Equal(WorkTaskStatus.Blocked, root.Status);
    }

    [Fact]
    public async Task StageContextRequiresTheAssignedInstallationAndActiveAttempt()
    {
        await using var db = CreateDb();
        var (ticket, owner, _) = Seed(db, false);
        ticket.Board!.Kind = WorkBoardKind.Standard;
        var work = new AgentWorkItem
        {
            Id = Guid.NewGuid(), OrganizationId = ticket.OrganizationId.ToString(),
            AgentInstallationId = owner.AgentInstallationId!.Value, SourceType = "WorkStageExecution"
        };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id };
        var stage = new WorkStageExecution
        {
            Id = Guid.NewGuid(), ItemExecution = execution, AgentInstallationId = work.AgentInstallationId
        };
        var attempt = new WorkExecutionAttempt
        {
            Id = Guid.NewGuid(), StageExecution = stage, AgentWorkItemId = work.Id,
            Status = WorkExecutionAttemptStatus.Running
        };
        work.SourceId = stage.Id.ToString();
        db.AddRange(execution, stage, attempt);
        await db.SaveChangesAsync();
        Assert.Equal(ticket.Id, (await AgentTicketFeedback.ResolveAsync(db, work, default))!.Id);
        work.AgentInstallationId = Guid.NewGuid();
        Assert.Null(await AgentTicketFeedback.ResolveAsync(db, work, default));
        work.AgentInstallationId = owner.AgentInstallationId.Value;
        attempt.Status = WorkExecutionAttemptStatus.Completed;
        await db.SaveChangesAsync();
        Assert.Null(await AgentTicketFeedback.ResolveAsync(db, work, default));
    }

    [Fact]
    public async Task DifferentFailureDoesNotEscalate()
    {
        await using var db = CreateDb();
        var (ticket, owner, _) = Seed(db, false);
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId!.Value,
            "one", Error, true, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        Assert.False(await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "two", "agent-failure:v1;code=agent.payload_invalid", true, DateTimeOffset.UtcNow.AddMinutes(1), default));
        await db.SaveChangesAsync();
        Assert.Equal(WorkTaskStatus.Running, ticket.Status);
        Assert.Empty(db.UserNotifications);
    }

    [Fact]
    public async Task ContextIncludesAllLiveCommentsAndRejectsOtherInstallationsAndTenants()
    {
        await using var db = CreateDb();
        var (ticket, owner, _) = Seed(db, false);
        var work = new AgentWorkItem
        {
            Id = Guid.NewGuid(), OrganizationId = ticket.OrganizationId.ToString("D"),
            AgentInstallationId = owner.AgentInstallationId!.Value, Kind = AgentWorkKind.Event,
            SourceId = ticket.ClaimEventId!.Value.ToString("D")
        };
        for (var i = 0; i < 250; i++)
            db.WorkItemComments.Add(new WorkItemComment
            {
                Id = Guid.NewGuid(), OrganizationId = ticket.OrganizationId, WorkItemId = ticket.Id,
                AuthorKind = GrantSubjectKind.OrganizationUser, AuthorSubjectId = owner.Id,
                AuthorDisplayName = "Reviewer", Body = $"Discussion entry {i}.",
                IdempotencyKey = i.ToString(), CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i),
                DeletedAt = i == 17 ? DateTimeOffset.UtcNow : null
            });
        AgentTicketFeedback.RecordClaim(db, ticket, owner.AgentInstallationId.Value,
            ticket.ClaimEventId.Value, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        ticket.ClaimEventId = null; // Association survives the SDK releasing the claim before reporting failure.
        await db.SaveChangesAsync();
        var context = await AgentTicketFeedback.ReadContextAsync(db, work, default);
        Assert.Contains("Discussion entry 0.", context);
        Assert.Contains("Discussion entry 249.", context);
        Assert.DoesNotContain("Discussion entry 17.", context);
        work.AgentInstallationId = Guid.NewGuid();
        Assert.Null(await AgentTicketFeedback.ReadContextAsync(db, work, default));
        work.AgentInstallationId = owner.AgentInstallationId.Value;
        work.OrganizationId = Guid.NewGuid().ToString();
        Assert.Null(await AgentTicketFeedback.ReadContextAsync(db, work, default));
    }

    private static (WorkTask, OrganizationUser, OrganizationUser) Seed(CSweetDbContext db, bool agentManager)
    {
        var org = Guid.NewGuid();
        var manager = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = org, IsActive = true, DisplayName = "Manager",
            EmployeeType = agentManager ? EmployeeType.Agent : EmployeeType.Human,
            AgentInstallationId = agentManager ? Guid.NewGuid() : null
        };
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = org, IsActive = true, DisplayName = "Daniel",
            EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid(),
            ReportsToOrganizationUserId = manager.Id
        };
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(), OrganizationId = org, Kind = WorkBoardKind.Personal,
            OwnerOrganizationUserId = owner.Id, ManagerOrganizationUserId = manager.Id
        };
        board.Columns.Add(new WorkBoardColumn
        {
            Id = Guid.NewGuid(), BoardId = board.Id, Category = WorkBoardColumnCategory.Blocked
        });
        var ticket = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = org, Board = board, BoardId = board.Id,
            Kind = WorkItemKind.Task, Title = "Build the game", Status = WorkTaskStatus.Running,
            AssignedEmployeeId = owner.Id, AssignedAgentInstallationId = owner.AgentInstallationId,
            ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            NextReviewAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.AddRange(owner, manager, board, ticket);
        db.SaveChanges();
        return (ticket, owner, manager);
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
