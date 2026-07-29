using CSweet.Application.Security;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkAutomationServiceTests
{
    [Fact]
    public async Task CreatedRuleUsesDedicatedUnprivilegedAutomationIdentity()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId);
        db.WorkBoards.Add(board);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var rule = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkAutomationRuleRequest(
                "Start new work", "item.created", board.Columns.ElementAt(0).Id,
                WorkItemActions.Move, board.Columns.ElementAt(1).Id));

        Assert.Equal(rule.Id, rule.AutomationIdentityId);
        Assert.False(rule.IsEnabled);
        Assert.False(rule.HasExecutionGrant);
        Assert.Contains(db.ScopedActionGrants, x =>
            x.SubjectId == setup.OrganizationUserId &&
            x.Action == WorkAutomationActions.Manage);
        Assert.DoesNotContain(db.ScopedActionGrants, x =>
            x.SubjectKind == GrantSubjectKind.AutomationIdentity &&
            x.SubjectId == rule.AutomationIdentityId);
    }

    [Fact]
    public async Task DispatcherRecordsDenialWhenExecutionGrantIsMissing()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId);
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var rule = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkAutomationRuleRequest(
                "Start new work", "item.created", board.Columns.ElementAt(0).Id,
                WorkItemActions.Move, board.Columns.ElementAt(1).Id, true));
        db.WorkItemActivities.Add(Activity(setup.OrganizationId, board.Id, item.Id, rule.CreatedAt));
        await db.SaveChangesAsync();

        var count = await CreateDispatcher(db).DispatchBatchAsync();

        Assert.Equal(1, count);
        var execution = Assert.Single(db.WorkAutomationExecutions);
        Assert.Equal(WorkAutomationExecutionStatus.Denied, execution.Status);
        Assert.Equal("grant_required", execution.ErrorCode);
        Assert.Equal(board.Columns.ElementAt(0).Id, item.BoardColumnId);
    }

    [Fact]
    public async Task DispatcherUsesCurrentGrantAndExecutesEachRuleEventOnce()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId);
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var rule = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkAutomationRuleRequest(
                "Start new work", "item.created", board.Columns.ElementAt(0).Id,
                WorkItemActions.Move, board.Columns.ElementAt(1).Id, true));
        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.AutomationIdentity,
            SubjectId = rule.AutomationIdentityId,
            Action = WorkItemActions.Move,
            ScopeKind = GrantScopeKind.Board,
            ScopeId = board.Id,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedBySubjectId = setup.OrganizationUserId,
            GrantedAt = DateTimeOffset.UtcNow
        });
        db.WorkItemActivities.Add(Activity(setup.OrganizationId, board.Id, item.Id, rule.CreatedAt));
        await db.SaveChangesAsync();
        var dispatcher = CreateDispatcher(db);

        var first = await dispatcher.DispatchBatchAsync();
        var replay = await dispatcher.DispatchBatchAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, replay);
        var execution = Assert.Single(db.WorkAutomationExecutions);
        Assert.Equal(WorkAutomationExecutionStatus.Succeeded, execution.Status);
        Assert.NotNull(execution.AuthorizingGrantId);
        Assert.Equal(board.Columns.ElementAt(1).Id, item.BoardColumnId);
        Assert.Equal(WorkTaskStatus.Running, item.Status);
        Assert.Contains(db.WorkItemActivities, x =>
            x.ActorKind == GrantSubjectKind.AutomationIdentity &&
            x.ActorSubjectId == rule.AutomationIdentityId &&
            x.EventType == "item.moved");
        Assert.Single(db.ApplicationRealtimeOutbox);
    }

    private static WorkAutomationService CreateService(CSweetDbContext db)
    {
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        return new WorkAutomationService(db, authorization, new TestAuditEventWriter());
    }

    private static WorkAutomationDispatcher CreateDispatcher(CSweetDbContext db)
    {
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        return new WorkAutomationDispatcher(
            db, authorization, new TestAuditEventWriter());
    }

    private static Setup SeedOwner(CSweetDbContext db)
    {
        var setup = new Setup(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.CoreOrganizations.Add(new Organization
        {
            Id = setup.OrganizationId,
            Name = "Test company",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = setup.OrganizationUserId,
            OrganizationId = setup.OrganizationId,
            ApplicationUserId = setup.ApplicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return setup;
    }

    private static WorkBoard Board(Guid organizationId)
    {
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = "Delivery",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        board.Columns.Add(new WorkBoardColumn
        {
            Id = Guid.NewGuid(),
            BoardId = board.Id,
            Name = "To Do",
            Category = WorkBoardColumnCategory.ToDo,
            Position = 0
        });
        board.Columns.Add(new WorkBoardColumn
        {
            Id = Guid.NewGuid(),
            BoardId = board.Id,
            Name = "Doing",
            Category = WorkBoardColumnCategory.InProgress,
            Position = 1
        });
        return board;
    }

    private static WorkTask Item(Guid organizationId, WorkBoard board) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        BoardId = board.Id,
        BoardColumnId = board.Columns.ElementAt(0).Id,
        Kind = WorkItemKind.Task,
        Title = "New work",
        Description = "",
        Status = WorkTaskStatus.Ready,
        Priority = WorkTaskPriority.Medium,
        BoardRank = 1024,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static WorkItemActivity Activity(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        DateTimeOffset ruleCreatedAt) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        BoardId = boardId,
        WorkItemId = itemId,
        EventType = "item.created",
        Action = WorkItemActions.Create,
        ActorKind = GrantSubjectKind.OrganizationUser,
        ActorSubjectId = Guid.NewGuid(),
        ActorDisplayName = "Owner",
        DataJson = "{}",
        OccurredAt = ruleCreatedAt.AddMilliseconds(1)
    };

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed record Setup(
        Guid OrganizationId,
        Guid OrganizationUserId,
        Guid ApplicationUserId);
}
