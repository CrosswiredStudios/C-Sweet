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

public sealed class WorkItemCollaborationServiceTests
{
    [Fact]
    public async Task CommentIsIdempotentAndCreatesActivityAndRealtimeEvent()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId, "Delivery");
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = CreateService(db, audit);
        var request = new AddWorkItemCommentRequest(
            "The acceptance criteria are ready.", "comment-1");

        var first = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId, request);
        var replay = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId, request);

        Assert.NotNull(first);
        Assert.Equal(first, replay);
        Assert.Single(await db.WorkItemComments.ToListAsync());
        Assert.Single(await db.WorkItemActivities.ToListAsync());
        Assert.Single(await db.ApplicationRealtimeOutbox.ToListAsync());
        Assert.Contains(audit.Events, x => x.EventType == WorkItemActions.Comment);
        var collaboration = await service.GetAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId);
        Assert.Single(collaboration!.Comments);
        Assert.Equal("comment.created", Assert.Single(collaboration.Activity).EventType);
    }

    [Fact]
    public async Task TransferMovesCanonicalItemOnceAndNotifiesBothBoards()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var source = Board(setup.OrganizationId, "Intake");
        var target = Board(setup.OrganizationId, "Delivery");
        var item = Item(setup.OrganizationId, source);
        item.SprintId = Guid.NewGuid();
        db.WorkBoards.AddRange(source, target);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var request = new TransferWorkItemRequest(
            target.Id, null, item.Revision, "transfer-1");

        var transferred = await service.TransferAsync(
            setup.OrganizationId, source.Id, item.Id, setup.ApplicationUserId, request);
        var replay = await service.TransferAsync(
            setup.OrganizationId, source.Id, item.Id, setup.ApplicationUserId, request);

        Assert.NotNull(transferred);
        Assert.Equal(target.Id, transferred.BoardId);
        Assert.Equal(transferred, replay);
        var persisted = await db.CoreWorkTasks.SingleAsync();
        Assert.Equal(target.Id, persisted.BoardId);
        Assert.Equal(target.Columns.Single().Id, persisted.BoardColumnId);
        Assert.Null(persisted.SprintId);
        Assert.Single(await db.WorkItemActivities.ToListAsync());
        Assert.Equal(2, await db.ApplicationRealtimeOutbox.CountAsync());
    }

    [Fact]
    public async Task HierarchicalItemCannotBeTransferredAlone()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var source = Board(setup.OrganizationId, "Product");
        var target = Board(setup.OrganizationId, "Delivery");
        var parent = Item(setup.OrganizationId, source);
        var child = Item(setup.OrganizationId, source);
        child.ParentWorkTaskId = parent.Id;
        db.WorkBoards.AddRange(source, target);
        db.CoreWorkTasks.AddRange(parent, child);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TransferAsync(
                setup.OrganizationId, source.Id, parent.Id, setup.ApplicationUserId,
                new TransferWorkItemRequest(
                    target.Id, null, parent.Revision, "transfer-hierarchy")));

        Assert.Contains("hierarchical", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source.Id, (await db.CoreWorkTasks.FindAsync(parent.Id))!.BoardId);
        Assert.Empty(db.WorkItemActivities);
    }

    private static WorkItemCollaborationService CreateService(
        CSweetDbContext db,
        TestAuditEventWriter audit)
    {
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        return new WorkItemCollaborationService(db, authorization, audit);
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

    private static WorkBoard Board(Guid organizationId, string name)
    {
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
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
        return board;
    }

    private static WorkTask Item(Guid organizationId, WorkBoard board) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        BoardId = board.Id,
        BoardColumnId = board.Columns.Single().Id,
        Kind = WorkItemKind.Task,
        Title = "Canonical item",
        Description = "",
        Status = WorkTaskStatus.Ready,
        Priority = WorkTaskPriority.Medium,
        BoardRank = 1024,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
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
