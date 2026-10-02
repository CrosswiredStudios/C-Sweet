using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

/// <summary>
/// Reproduces VGF943299B17-5: a specialist stage blocked after two provider timeouts sat in "In Progress" on a
/// board without a Blocked column, its Retry button was refused for the CEO, and the card could not be moved.
/// </summary>
public sealed class BlockedTicketRetryTests
{
    [Fact]
    public async Task BlockedSprintCard_ParksInABlockedColumnTheBoardDidNotHave()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var execution = await db.WorkSprintExecutions
            .Include(x => x.Items).ThenInclude(x => x.WorkItem)
            .Include(x => x.Items).ThenInclude(x => x.Stages)
            .SingleAsync();

        await WorkOrchestrationBoardState.ParkBlockedCardsAsync(db, execution, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var blocked = await db.WorkBoardColumns.SingleAsync(x => x.Category == WorkBoardColumnCategory.Blocked);
        Assert.Equal("Blocked", blocked.Name);
        Assert.Equal(3, blocked.Position);
        var card = await db.CoreWorkTasks.SingleAsync();
        Assert.Equal(blocked.Id, card.BoardColumnId);
        Assert.Equal(WorkTaskStatus.Blocked, card.Status);
        Assert.Equal(2, (await db.WorkBoards.SingleAsync()).Revision);

        // Parking is idempotent: a second pass neither adds a column nor touches the card.
        execution = await db.WorkSprintExecutions.Include(x => x.Items).ThenInclude(x => x.WorkItem)
            .Include(x => x.Items).ThenInclude(x => x.Stages).SingleAsync();
        await WorkOrchestrationBoardState.ParkBlockedCardsAsync(db, execution, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        Assert.Single(await db.WorkBoardColumns.Where(x => x.Category == WorkBoardColumnCategory.Blocked).ToListAsync());
        Assert.Equal(card.Revision, (await db.CoreWorkTasks.SingleAsync()).Revision);
        _ = seeded;
    }

    [Fact]
    public async Task PersonWithRetryGrant_RetriesBlockedTicketEvenAfterAutomaticAttemptsAreSpent()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var service = new WorkOrchestrationService(db, TimeProvider.System, new Authorization(allowed: true));

        var stage = await service.RetryItemAsync(seeded.OrganizationId, seeded.BoardId, seeded.ItemId,
            seeded.CeoApplicationUserId, new WorkOrchestrationControlRequest(seeded.CardRevision, "ceo-retry"));

        Assert.NotNull(stage);
        Assert.Equal("Pending", stage!.Status);
        db.ChangeTracker.Clear();
        var card = await db.CoreWorkTasks.SingleAsync();
        Assert.Equal(WorkTaskStatus.Ready, card.Status);
        Assert.Null(card.BlockReason);
        Assert.Equal(seeded.ReadyColumnId, card.BoardColumnId);
        var item = await db.WorkItemExecutions.SingleAsync();
        Assert.Equal(WorkItemExecutionStatus.Pending, item.Status);
        Assert.Null(item.BlockedReason);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x => x.EventType == "stage.retry.requested").ToListAsync());
    }

    [Fact]
    public async Task PersonWithoutRetryGrant_CannotRetry()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var service = new WorkOrchestrationService(db, TimeProvider.System, new Authorization(allowed: false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryItemAsync(seeded.OrganizationId,
            seeded.BoardId, seeded.ItemId, seeded.CeoApplicationUserId,
            new WorkOrchestrationControlRequest(seeded.CardRevision, "denied")));
        db.ChangeTracker.Clear();
        Assert.Equal(WorkStageExecutionStatus.Blocked, (await db.WorkStageExecutions.SingleAsync()).Status);
    }

    [Fact]
    public async Task StageRetryResponse_CarriesTheAssignmentRevisionTheRetryMustEcho()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var service = new WorkOrchestrationService(db, TimeProvider.System, new Authorization(allowed: true));
        var sprint = await db.WorkSprintExecutions.SingleAsync();

        var execution = await service.GetExecutionAsync(seeded.OrganizationId, seeded.BoardId, sprint.SprintId,
            seeded.CeoApplicationUserId);
        var stage = execution!.Items.Single().Stages.Last();
        var retried = await service.RetryAsync(seeded.OrganizationId, seeded.BoardId, stage.Id,
            seeded.CeoApplicationUserId, new WorkOrchestrationControlRequest(stage.AssignmentRevision, "stage-retry"));

        Assert.Equal(seeded.AssignmentRevision, stage.AssignmentRevision);
        Assert.Equal("Pending", retried.Status);
    }

    [Fact]
    public async Task MovingBlockedSprintCardToReady_RetriesIt()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var orchestration = new WorkOrchestrationService(db, TimeProvider.System, new Authorization(allowed: true));
        var boards = new WorkBoardService(db, new ScopedActionAuthorizationService(db), new NullAudit(),
            orchestration);

        var moved = await boards.MoveItemAsync(seeded.OrganizationId, seeded.BoardId, seeded.ItemId,
            seeded.CeoApplicationUserId, new MoveBoardWorkItemRequest(seeded.ReadyColumnId, null, seeded.CardRevision));

        Assert.NotNull(moved);
        Assert.Equal("Ready", moved!.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(WorkStageExecutionStatus.Pending, (await db.WorkStageExecutions.SingleAsync()).Status);
    }

    [Fact]
    public async Task MovingRunningSprintCard_IsStillRefused()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db);
        var card = await db.CoreWorkTasks.SingleAsync();
        card.Status = WorkTaskStatus.Running;
        await db.SaveChangesAsync();
        var orchestration = new WorkOrchestrationService(db, TimeProvider.System, new Authorization(allowed: true));
        var boards = new WorkBoardService(db, new ScopedActionAuthorizationService(db), new NullAudit(),
            orchestration);

        await Assert.ThrowsAsync<InvalidOperationException>(() => boards.MoveItemAsync(seeded.OrganizationId,
            seeded.BoardId, seeded.ItemId, seeded.CeoApplicationUserId,
            new MoveBoardWorkItemRequest(seeded.ReadyColumnId, null, card.Revision)));
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    private static async Task<Seeded> SeedAsync(CSweetDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var ceoId = Guid.NewGuid();
        var ceoApplicationUserId = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var specialistId = Guid.NewGuid();
        var specialistInstallationId = Guid.NewGuid();
        var policyRevisionId = Guid.NewGuid();
        var readyColumnId = Guid.NewGuid();
        var inProgressColumnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        const long assignmentRevision = 1;
        const string timeout = "The provider exceeded its generation time limit.";

        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Games", Status = OrganizationStatus.Active, CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser
            {
                Id = ceoId, OrganizationId = organizationId, ApplicationUserId = ceoApplicationUserId,
                DisplayName = "CEO", EmployeeType = EmployeeType.Human,
                PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true, CreatedAt = now
            },
            new OrganizationUser
            {
                Id = producerId, OrganizationId = organizationId, AgentInstallationId = Guid.NewGuid(),
                DisplayName = "Producer", EmployeeType = EmployeeType.Agent,
                PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true, CreatedAt = now
            },
            new OrganizationUser
            {
                Id = specialistId, OrganizationId = organizationId, AgentInstallationId = specialistInstallationId,
                DisplayName = "Technical Director", EmployeeType = EmployeeType.Agent,
                PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true, CreatedAt = now
            });
        db.WorkBoards.Add(new WorkBoard
        {
            Id = boardId, OrganizationId = organizationId, ManagerOrganizationUserId = producerId,
            Key = "VGF", Name = "Game Pitch", CreatedAt = now, UpdatedAt = now,
            Columns =
            [
                new WorkBoardColumn { Id = readyColumnId, Name = "Ready", Category = WorkBoardColumnCategory.ToDo, Position = 0 },
                new WorkBoardColumn { Id = inProgressColumnId, Name = "In Progress", Category = WorkBoardColumnCategory.InProgress, Position = 1 },
                new WorkBoardColumn { Id = Guid.NewGuid(), Name = "Done", Category = WorkBoardColumnCategory.Done, Position = 2 }
            ]
        });
        db.CoreWorkTasks.Add(new WorkTask
        {
            Id = itemId, OrganizationId = organizationId, BoardId = boardId, BoardColumnId = inProgressColumnId,
            AssignmentRevision = assignmentRevision, Title = "Technical Architecture Plan", Description = "",
            Status = WorkTaskStatus.Blocked, BlockReason = timeout, Priority = WorkTaskPriority.High,
            BoardRank = 1024, CreatedAt = now, UpdatedAt = now
        });
        db.WorkOrchestrationPolicyRevisions.Add(new WorkOrchestrationPolicyRevision
        {
            Id = policyRevisionId, OrganizationId = organizationId, BoardId = boardId, PolicyId = Guid.NewGuid(),
            Revision = 1, Name = "Delivery", InitialStageKey = "ready", IsPublished = true, CreatedAt = now
        });
        db.WorkOrchestrationStages.AddRange(
            new WorkOrchestrationStage
            {
                Id = Guid.NewGuid(), PolicyRevisionId = policyRevisionId, Key = "ready", Name = "Ready",
                Type = WorkOrchestrationStageType.Queue, ColumnId = readyColumnId, MaximumAttempts = 1
            },
            new WorkOrchestrationStage
            {
                Id = Guid.NewGuid(), PolicyRevisionId = policyRevisionId, Key = "specialist-execution",
                Name = "Specialist Execution", Type = WorkOrchestrationStageType.AgentExecution,
                ColumnId = inProgressColumnId, MaximumAttempts = 3
            });
        db.WorkOrchestrationTransitions.Add(new WorkOrchestrationTransition
        {
            Id = Guid.NewGuid(), PolicyRevisionId = policyRevisionId, FromStageKey = "ready",
            OutcomeCode = "ready", ToStageKey = "specialist-execution"
        });
        var sprintExecutionId = Guid.NewGuid();
        var itemExecutionId = Guid.NewGuid();
        var stageId = Guid.NewGuid();
        db.WorkSprintExecutions.Add(new WorkSprintExecution
        {
            Id = sprintExecutionId, OrganizationId = organizationId, BoardId = boardId, SprintId = Guid.NewGuid(),
            PolicyRevisionId = policyRevisionId, StartedByOrganizationUserId = producerId,
            Status = WorkSprintExecutionStatus.Active, StartedAt = now, UpdatedAt = now
        });
        db.WorkItemExecutions.Add(new WorkItemExecution
        {
            Id = itemExecutionId, SprintExecutionId = sprintExecutionId, WorkItemId = itemId,
            ItemIdentifier = "VGF-5", CurrentStageKey = "specialist-execution",
            Status = WorkItemExecutionStatus.Blocked, BlockedReason = timeout, CreatedAt = now, UpdatedAt = now
        });
        db.WorkStageExecutions.Add(new WorkStageExecution
        {
            Id = stageId, ItemExecutionId = itemExecutionId, StageKey = "specialist-execution",
            StageType = WorkOrchestrationStageType.AgentExecution, Status = WorkStageExecutionStatus.Blocked,
            PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            AgentInstallationId = specialistInstallationId, LastError = timeout,
            CreatedAt = now, UpdatedAt = now
        });
        // Every automatic attempt is spent: a person's retry must still be accepted.
        for (var attempt = 1; attempt <= 3; attempt++)
            db.WorkExecutionAttempts.Add(new WorkExecutionAttempt
            {
                Id = Guid.NewGuid(), StageExecutionId = stageId, Attempt = attempt,
                IdempotencyKey = $"attempt-{attempt}", Status = WorkExecutionAttemptStatus.Failed,
                ErrorMessage = timeout, CreatedAt = now
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var card = await db.CoreWorkTasks.AsNoTracking().SingleAsync();
        return new(organizationId, boardId, itemId, ceoApplicationUserId, readyColumnId, card.Revision, assignmentRevision);
    }

    private sealed class Authorization(bool allowed) : IScopedActionAuthorizationService
    {
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid organizationId, GrantSubjectKind subjectKind,
            Guid subjectId, string action, GrantScopeKind resourceScopeKind, Guid? resourceScopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScopedAuthorizationDecision(allowed && action == WorkOrchestrationActions.Retry, action));
    }

    private sealed class NullAudit : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary,
            string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.EventId ?? Guid.NewGuid());
    }

    private sealed record Seeded(Guid OrganizationId, Guid BoardId, Guid ItemId, Guid CeoApplicationUserId,
        Guid ReadyColumnId, long CardRevision, long AssignmentRevision);
}
