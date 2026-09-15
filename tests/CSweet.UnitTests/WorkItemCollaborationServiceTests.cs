using CSweet.Application.Security;
using CSweet.Contracts.Realtime;
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
        Assert.Single(await db.ApplicationRealtimeOutbox.ToListAsync(),
            x => x.EventType == AppRealtimeEvents.WorkBoardChanged);
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
        Assert.Equal(2, await db.ApplicationRealtimeOutbox.CountAsync(
            x => x.EventType == AppRealtimeEvents.WorkBoardChanged));
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

    [Fact]
    public async Task CommentUpdateRewritesTheBodyAndIsReplaySafe()
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
        var comment = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new AddWorkItemCommentRequest("First wording.", "comment-1"));
        Assert.NotNull(comment);
        var request = new UpdateWorkItemCommentRequest(
            "Corrected wording.", comment.Revision, "update-1");

        var updated = await service.UpdateCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId, request);
        var replay = await service.UpdateCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId, request);

        Assert.NotNull(updated);
        Assert.Equal("Corrected wording.", updated!.Body);
        Assert.Equal(comment.Revision + 1, updated.Revision);
        Assert.NotNull(updated.EditedAt);
        Assert.True(updated.CanEdit);
        Assert.True(updated.CanDelete);
        Assert.Equal(updated, replay);
        Assert.Single(await db.WorkItemActivities.Where(x => x.EventType == "comment.updated").ToListAsync());
        Assert.Single(await db.WorkItemActivities.Where(x => x.EventType == "comment.created").ToListAsync());
        Assert.Contains(audit.Events, x => x.EventType == WorkItemActions.UpdateComment);
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(),
            x => x.DataJson.Contains("comment.updated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommentDeleteSoftDeletesTheRowAndKeepsTheTrail()
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
        var comment = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new AddWorkItemCommentRequest("Withdrawn guidance.", "comment-1"));
        Assert.NotNull(comment);

        var deleted = await service.DeleteCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId,
            new DeleteWorkItemCommentRequest(comment.Revision, "delete-1"));

        Assert.NotNull(deleted);
        var persisted = await db.WorkItemComments.SingleAsync();
        Assert.NotNull(persisted.DeletedAt);
        Assert.Equal(comment.Revision + 1, persisted.Revision);
        var collaboration = await service.GetAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId);
        Assert.Empty(collaboration!.Comments);
        Assert.Contains(collaboration.Activity, x => x.EventType == "comment.deleted");
        Assert.Contains(audit.Events, x => x.EventType == WorkItemActions.DeleteComment);
    }

    [Fact]
    public async Task OnlyTheAuthorCanChangeAComment()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var member = SeedMember(db, setup);
        var board = Board(setup.OrganizationId, "Delivery");
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        // The owner's pass provisions the member, so the denial below is authorship, not a missing grant.
        var comment = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new AddWorkItemCommentRequest("Owner guidance.", "comment-1"));
        Assert.NotNull(comment);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, member.ApplicationUserId,
            new UpdateWorkItemCommentRequest("Hijacked.", comment.Revision, "update-2")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, member.ApplicationUserId,
            new DeleteWorkItemCommentRequest(comment.Revision, "delete-2")));

        var collaboration = await service.GetAsync(
            setup.OrganizationId, board.Id, item.Id, member.ApplicationUserId);
        var other = Assert.Single(collaboration!.Comments);
        Assert.False(other.CanEdit);
        Assert.False(other.CanDelete);
        Assert.True(collaboration.CanComment);
        Assert.Equal("Owner guidance.", (await db.WorkItemComments.SingleAsync()).Body);
    }

    [Fact]
    public async Task CommentMutationRejectsAStaleRevisionAndDeletedRows()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId, "Delivery");
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var comment = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new AddWorkItemCommentRequest("Original.", "comment-1"));
        Assert.NotNull(comment);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.UpdateCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId,
            new UpdateWorkItemCommentRequest("Stale.", comment.Revision + 5, "update-2")));

        await service.DeleteCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId,
            new DeleteWorkItemCommentRequest(comment.Revision, "delete-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId,
            new UpdateWorkItemCommentRequest("After delete.", comment.Revision + 1, "update-3")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteCommentAsync(
            setup.OrganizationId, board.Id, item.Id, comment.Id, setup.ApplicationUserId,
            new DeleteWorkItemCommentRequest(comment.Revision + 1, "delete-3")));
    }

    [Fact]
    public async Task MemberGetsCommentAuthorityOnATeamBoardByDefault()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var member = SeedMember(db, setup);
        var board = Board(setup.OrganizationId, "Delivery");
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        await service.GetAsync(setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId);

        var collaboration = await service.GetAsync(
            setup.OrganizationId, board.Id, item.Id, member.ApplicationUserId);
        var posted = await service.AddCommentAsync(
            setup.OrganizationId, board.Id, item.Id, member.ApplicationUserId,
            new AddWorkItemCommentRequest("Member view.", "member-1"));

        Assert.True(collaboration!.CanComment);
        Assert.NotNull(posted);
        Assert.True(posted!.CanEdit);
        Assert.True(posted.CanDelete);
    }

    [Fact]
    public async Task PersonalBoardThreadAuthorizesWithThePersonalTodoReadGrant()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var member = SeedMember(db, setup);
        var board = Board(setup.OrganizationId, "Personal board", WorkBoardKind.Personal);
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        // PersonalTodoService provisions this board-scoped read grant for the board owner.
        db.ScopedActionGrants.Add(Grant(
            setup.OrganizationId, setup.OrganizationUserId, PersonalTodoActions.Read, board.Id));
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());

        var collaboration = await service.GetAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId);

        Assert.NotNull(collaboration);
        Assert.Equal(setup.OrganizationUserId, collaboration!.CurrentOrganizationUserId);
        // Ordinary item read does not open a personal board, and the owner would need an
        // explicit comment grant to post one.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(
            setup.OrganizationId, board.Id, item.Id, member.ApplicationUserId));
        Assert.Empty(await db.WorkItemComments.ToListAsync());
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

    private static Setup SeedMember(CSweetDbContext db, Setup setup)
    {
        var applicationUserId = Guid.NewGuid();
        var organizationUserId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = organizationUserId,
            OrganizationId = setup.OrganizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Member",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return setup with
        {
            OrganizationUserId = organizationUserId,
            ApplicationUserId = applicationUserId
        };
    }

    private static ScopedActionGrant Grant(
        Guid organizationId,
        Guid subjectId,
        string action,
        Guid? boardId = null) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        SubjectKind = GrantSubjectKind.OrganizationUser,
        SubjectId = subjectId,
        Action = action,
        ScopeKind = boardId.HasValue ? GrantScopeKind.Board : GrantScopeKind.Organization,
        ScopeId = boardId,
        GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
        GrantedAt = DateTimeOffset.UtcNow
    };

    private static WorkBoard Board(Guid organizationId, string name, WorkBoardKind kind = WorkBoardKind.Standard)
    {
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            Description = "",
            Kind = kind,
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
