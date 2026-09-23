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

    [Fact]
    public async Task ReportedBlockerPreservesEvidenceAndRecoveryStepsInPersistedComment()
    {
        await using var db = CreateDb();
        var (ticket, owner, _) = Seed(db, false);
        const string report = "Development is blocked: Task validation failed.\n\n### What failed\n\nnode --test exited 1: expected three lives, got two.\n\n### Next step\n\nCorrect the initial lives and move the ticket to To Do.";
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId!.Value,
            "reported-one", "reported:" + report, false, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        var comment = Assert.Single(db.WorkItemComments);
        Assert.Contains(report, comment.Body);
        Assert.Contains("### Next step", comment.Body);
        // Duplicate delivery must not post a second comment.
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "reported-one", "reported:" + report, false, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        Assert.Single(db.WorkItemComments);
    }

    [Fact]
    public void OversizedReportedBlockerIsBoundedAndPointsToTicket()
    {
        var message = AgentTicketFeedback.FailureSentence("reported:" + new string('x', 7000));
        Assert.True(message.Length < 6200);
        Assert.Contains("See the ticket blocker", message);
    }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterRepeatedFailureReusesExistingEscalationChannel(bool agentManager)
    {
        await using var db = CreateDb();
        var (ticket, owner, manager) = Seed(db, agentManager);
        var now = DateTimeOffset.UtcNow;
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId!.Value,
            "attempt-1", Error, true, now, default);
        await db.SaveChangesAsync();
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "attempt-2", Error, true, now.AddMinutes(1), default);
        await db.SaveChangesAsync();
        var original = Assert.Single(db.CoreConversations);

        ticket.Status = WorkTaskStatus.Running;
        ticket.BlockReason = null;
        await db.SaveChangesAsync();
        Assert.True(await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "attempt-3", Error, true, now.AddMinutes(2), default));
        await db.SaveChangesAsync();

        Assert.Equal(original.Id, Assert.Single(db.CoreConversations).Id);
        Assert.Equal(2, await db.CoreConversationMessages.CountAsync());
        Assert.Equal(2, await db.ConversationMessageMentions.CountAsync());
        Assert.Equal(2, await db.ConversationParticipants.CountAsync());
        Assert.Equal(agentManager ? 2 : 0, await db.AgentPlatformEventOutbox.CountAsync());
        Assert.All(db.UserNotifications, notification =>
        {
            Assert.Equal(manager.Id, notification.RecipientOrganizationUserId);
            Assert.EndsWith(original.Id.ToString("D"), notification.ActionUri);
        });
    }

    [Fact]
    public async Task SameTitleOnDifferentTicketsKeepsEscalationsSeparate()
    {
        await using var db = CreateDb();
        var (first, owner, _) = Seed(db, false);
        var second = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = first.OrganizationId, BoardId = first.BoardId,
            Kind = WorkItemKind.Task, Title = first.Title, Status = WorkTaskStatus.Running,
            AssignedEmployeeId = owner.Id, AssignedAgentInstallationId = owner.AgentInstallationId
        };
        db.CoreWorkTasks.Add(second);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var ticket in new[] { first, second })
        {
            await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId!.Value,
                $"{ticket.Id:N}-1", Error, true, now, default);
            await db.SaveChangesAsync();
            await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
                $"{ticket.Id:N}-2", Error, true, now.AddMinutes(1), default);
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, await db.CoreConversations.CountAsync());
    }

    [Fact]
    public async Task ExistingDuplicateEscalationsContinueInTheNewestChannel()
    {
        await using var db = CreateDb();
        var (ticket, owner, manager) = Seed(db, false);
        var now = DateTimeOffset.UtcNow;
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId!.Value,
            "attempt-1", Error, true, now, default);
        await db.SaveChangesAsync();
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "attempt-2", Error, true, now.AddMinutes(1), default);
        await db.SaveChangesAsync();
        var older = Assert.Single(db.CoreConversations);
        var newer = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = ticket.OrganizationId,
            Kind = ConversationKind.AgentChannel, IsPrivate = true,
            InitiatedByOrganizationUserId = owner.Id, Title = older.Title,
            CreatedAt = now.AddMinutes(2), UpdatedAt = now.AddMinutes(2)
        };
        foreach (var id in new[] { owner.Id, manager.Id })
            newer.Participants.Add(new ConversationParticipant
            {
                Id = Guid.NewGuid(), ConversationId = newer.Id, OrganizationUserId = id
            });
        newer.Messages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = newer.Id, SenderOrganizationUserId = owner.Id,
            IdempotencyKey = $"ticket-repeat:historical:{ticket.Id:N}", CreatedAt = now.AddMinutes(2)
        });
        db.CoreConversations.Add(newer);
        await db.SaveChangesAsync();

        ticket.Status = WorkTaskStatus.Running;
        ticket.BlockReason = null;
        await db.SaveChangesAsync();
        await AgentTicketFeedback.RecordFailureAsync(db, ticket, owner.AgentInstallationId.Value,
            "attempt-3", Error, true, now.AddMinutes(3), default);
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.CoreConversations.CountAsync());
        Assert.Equal(1, await db.CoreConversationMessages.CountAsync(x => x.ConversationId == older.Id));
        Assert.Equal(2, await db.CoreConversationMessages.CountAsync(x => x.ConversationId == newer.Id));
        Assert.EndsWith(newer.Id.ToString("D"), db.UserNotifications.OrderByDescending(x => x.CreatedAt).First().ActionUri);
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
