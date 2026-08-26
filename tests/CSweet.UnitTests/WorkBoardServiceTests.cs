using CSweet.Application.Security;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using WorkItemTypeKeys = CSweet.WorkManagement.Contracts.WorkItemTypeKeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CSweet.UnitTests;

public sealed class WorkBoardServiceTests
{
    [Fact]
    public void WorkTaskStringEnumConstraints_AreInTheModelAndMigrationSet()
    {
        using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
                .Options);

        var model = db.GetService<IDesignTimeModel>().Model;
        var constraintNames = model.FindEntityType(typeof(WorkTask))!
            .GetCheckConstraints()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("CK_CoreWorkTasks_Kind", constraintNames);
        Assert.Contains("CK_CoreWorkTasks_Status", constraintNames);
        Assert.Contains("CK_CoreWorkTasks_Priority", constraintNames);
        Assert.Contains(
            "20260729050000_ConstrainWorkTaskEnumValues",
            db.Database.GetMigrations());
    }

    [Fact]
    public async Task OwnerDirectory_BootstrapsExplicitGrantsDefaultBoardAndExistingTasks()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            Title = "Existing task",
            Description = "",
            Status = WorkTaskStatus.Ready,
            Priority = WorkTaskPriority.Medium,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreWorkTasks.Add(task);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = CreateService(db, audit);

        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId,
            setup.ApplicationUserId,
            new WorkBoardDirectoryQuery());

        var board = Assert.Single(directory.Boards);
        Assert.True(board.IsDefault);
        Assert.True(directory.CanCreateBoard);
        Assert.Equal(1, board.ActiveItemCount);
        Assert.Equal(board.Id, (await db.CoreWorkTasks.SingleAsync()).BoardId);
        Assert.NotNull((await db.CoreWorkTasks.SingleAsync()).BoardColumnId);
        Assert.Equal(
            WorkBoardActions.All.Count + WorkItemActions.All.Count +
            WorkSprintActions.All.Count + WorkOrchestrationActions.All.Count,
            await db.ScopedActionGrants.CountAsync());
        Assert.Contains(audit.Events, x => x.EventType == WorkBoardActions.Read);
    }

    [Fact]
    public async Task OwnerDirectory_RepairsMissingOwnerGrantsWhenAgentGrantAlreadyExists()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        var productBoard = Board(setup.OrganizationId, "Product team");
        db.WorkBoards.Add(productBoard);
        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = Guid.NewGuid(),
            Action = WorkBoardActions.Create,
            ScopeKind = GrantScopeKind.Team,
            ScopeId = Guid.NewGuid(),
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedBySubjectId = setup.OrganizationUserId,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());

        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId,
            setup.ApplicationUserId,
            new WorkBoardDirectoryQuery());
        await service.ListDirectoryAsync(
            setup.OrganizationId,
            setup.ApplicationUserId,
            new WorkBoardDirectoryQuery());

        Assert.Contains(directory.Boards, x => x.Id == productBoard.Id);
        Assert.True(directory.CanCreateBoard);
        var ownerActions = await db.ScopedActionGrants
            .Where(x =>
                x.SubjectKind == GrantSubjectKind.OrganizationUser &&
                x.SubjectId == setup.OrganizationUserId &&
                x.ScopeKind == GrantScopeKind.Organization)
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Equal(
            WorkBoardActions.All
                .Concat(WorkItemActions.All)
                .Concat(WorkSprintActions.All)
                .Concat(WorkOrchestrationActions.All)
                .Order(),
            ownerActions.Order());
    }

    [Fact]
    public async Task Directory_ReturnsOnlyBoardsCoveredByReadGrant()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Contributor);
        var first = Board(setup.OrganizationId, "Visible");
        var second = Board(setup.OrganizationId, "Hidden");
        db.WorkBoards.AddRange(first, second);
        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.OrganizationUser,
            SubjectId = setup.OrganizationUserId,
            Action = WorkBoardActions.Read,
            ScopeKind = GrantScopeKind.Board,
            ScopeId = first.Id,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());

        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId,
            setup.ApplicationUserId,
            new WorkBoardDirectoryQuery(IncludeArchived: true));

        Assert.Equal("Visible", Assert.Single(directory.Boards).Name);
        Assert.False(directory.CanCreateBoard);
    }

    [Fact]
    public async Task OwnerCanCreateFavoriteAndArchiveNonDefaultBoard()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery());

        var created = await service.CreateAsync(
            setup.OrganizationId,
            setup.ApplicationUserId,
            new CreateWorkBoardRequest("Engineering", "Delivery work"));
        Assert.Equal(2, created.Columns.Count);

        Assert.True(await service.SetFavoriteAsync(
            setup.OrganizationId, created.Board.Id, setup.ApplicationUserId, true));
        Assert.True(await service.ArchiveAsync(
            setup.OrganizationId, created.Board.Id, setup.ApplicationUserId));

        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId,
            setup.ApplicationUserId,
            new WorkBoardDirectoryQuery(IncludeArchived: true, FavoritesOnly: true));
        var archived = Assert.Single(directory.Boards);
        Assert.True(archived.IsArchived);
        Assert.True(archived.IsFavorite);
    }

    [Fact]
    public async Task GetBoard_MapsStringBackedWorkTaskEnumsAfterQueryExecution()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery());
        var boardId = Assert.Single(directory.Boards).Id;
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == boardId);
        var column = board.Columns.OrderBy(x => x.Position).First();
        db.CoreWorkTasks.Add(new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            BoardId = boardId,
            BoardColumnId = column.Id,
            Kind = WorkItemKind.Task,
            Title = "Backlog item",
            Description = "",
            Status = WorkTaskStatus.Backlog,
            Priority = WorkTaskPriority.Critical,
            BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var detail = await service.GetAsync(
            setup.OrganizationId, boardId, setup.ApplicationUserId);

        var item = Assert.Single(Assert.IsType<WorkBoardDetailResponse>(detail).Items);
        Assert.Equal("Task", item.Kind);
        Assert.Equal(nameof(WorkTaskStatus.Backlog), item.Status);
        Assert.Equal(nameof(WorkTaskPriority.Critical), item.Priority);
    }

    [Fact]
    public async Task DefaultBoardCannotBeArchived()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ArchiveAsync(
                setup.OrganizationId,
                Assert.Single(directory.Boards).Id,
                setup.ApplicationUserId));

        Assert.Contains("default board", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = "Direct card transitions are replaced by orchestration policy transitions.")]
    public async Task OwnerCanConfigureWorkflowCreateStoryCompleteAndReopenIt()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery());
        var boardId = Assert.Single(directory.Boards).Id;
        var detail = await service.GetAsync(
            setup.OrganizationId, boardId, setup.ApplicationUserId);
        Assert.NotNull(detail);

        detail = await service.ConfigureColumnsAsync(
            setup.OrganizationId,
            boardId,
            setup.ApplicationUserId,
            new ConfigureWorkBoardColumnsRequest(
                detail.Board.Revision,
                [
                    new(detail.Columns[0].Id, "Ready", "ToDo"),
                    new(null, "Doing", "InProgress", "HardLimit", 2),
                    new(detail.Columns[1].Id, "Shipped", "Done")
                ]));
        Assert.NotNull(detail);
        var doing = detail.Columns.Single(x => x.Category == "InProgress");
        var done = detail.Columns.Single(x => x.Category == "Done");
        var ready = detail.Columns.Single(x => x.Category == "ToDo");

        var story = await service.CreateItemAsync(
            setup.OrganizationId,
            boardId,
            setup.ApplicationUserId,
            new CreateBoardWorkItemRequest("Grant-secured workflow", Kind: "Story"));
        Assert.Equal("Story", story.Kind);
        Assert.Equal(ready.Id, story.ColumnId);

        story = Assert.IsType<WorkBoardItemResponse>(await service.MoveItemAsync(
            setup.OrganizationId,
            boardId,
            story.Id,
            setup.ApplicationUserId,
            new MoveBoardWorkItemRequest(doing.Id, null, story.Revision)));
        Assert.Equal(nameof(WorkTaskStatus.Running), story.Status);

        story = Assert.IsType<WorkBoardItemResponse>(await service.MoveItemAsync(
            setup.OrganizationId,
            boardId,
            story.Id,
            setup.ApplicationUserId,
            new MoveBoardWorkItemRequest(done.Id, null, story.Revision)));
        Assert.Equal(nameof(WorkTaskStatus.Completed), story.Status);

        story = Assert.IsType<WorkBoardItemResponse>(await service.MoveItemAsync(
            setup.OrganizationId,
            boardId,
            story.Id,
            setup.ApplicationUserId,
            new MoveBoardWorkItemRequest(ready.Id, null, story.Revision)));
        Assert.Equal(nameof(WorkTaskStatus.Ready), story.Status);
    }

    [Fact]
    public async Task HardWipLimitRejectsAdditionalCard()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var directory = await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery());
        var boardId = Assert.Single(directory.Boards).Id;
        var detail = (await service.GetAsync(
            setup.OrganizationId, boardId, setup.ApplicationUserId))!;
        detail = (await service.ConfigureColumnsAsync(
            setup.OrganizationId,
            boardId,
            setup.ApplicationUserId,
            new ConfigureWorkBoardColumnsRequest(
                detail.Board.Revision,
                [
                    new(detail.Columns[0].Id, "Ready", "ToDo", "HardLimit", 1),
                    new(detail.Columns[1].Id, "Done", "Done")
                ])))!;

        await service.CreateItemAsync(
            setup.OrganizationId, boardId, setup.ApplicationUserId,
            new CreateBoardWorkItemRequest("First", Kind: "Epic")
            {
                TypeKey = WorkItemTypeKeys.GeneralEpicV1
            });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateItemAsync(
                setup.OrganizationId, boardId, setup.ApplicationUserId,
                new CreateBoardWorkItemRequest("Second", Kind: "Epic")
                {
                    TypeKey = WorkItemTypeKeys.GeneralEpicV1
                }));

        Assert.Contains("WIP limit", exception.Message);
    }

    [Fact]
    public async Task WorkItemCreationRequiresARegisteredTypeAndMatchingBaseKind()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var boardId = Assert.Single((await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery())).Boards).Id;

        var missing = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateItemAsync(
            setup.OrganizationId, boardId, setup.ApplicationUserId,
            new CreateBoardWorkItemRequest("Missing type", Kind: "Epic")));
        Assert.Contains("type key", missing.Message, StringComparison.OrdinalIgnoreCase);

        var mismatch = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateItemAsync(
            setup.OrganizationId, boardId, setup.ApplicationUserId,
            new CreateBoardWorkItemRequest("Mismatched kind", Kind: "Task")
            {
                TypeKey = WorkItemTypeKeys.GeneralEpicV1
            }));
        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOnlyMemberCannotCreateWorkItem()
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Contributor);
        var board = Board(setup.OrganizationId, "Shared");
        db.WorkBoards.Add(board);
        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.OrganizationUser,
            SubjectId = setup.OrganizationUserId,
            Action = WorkBoardActions.Read,
            ScopeKind = GrantScopeKind.Board,
            ScopeId = board.Id,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = CreateService(db, audit);
        await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new WorkBoardDirectoryQuery());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateItemAsync(
                setup.OrganizationId,
                board.Id,
                setup.ApplicationUserId,
                new CreateBoardWorkItemRequest("Not allowed")));

        Assert.Contains(audit.Events, x =>
            x.EventType == "work.authorization.denied" &&
            x.MetadataJson!.Contains(WorkItemActions.Create, StringComparison.Ordinal));
    }

    private static WorkBoardService CreateService(
        CSweetDbContext db,
        TestAuditEventWriter audit)
    {
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        return new WorkBoardService(db, authorization, audit);
    }

    private static (Guid OrganizationId, Guid OrganizationUserId, Guid ApplicationUserId)
        SeedOrganization(CSweetDbContext db, OrganizationPermissionLevel permission)
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var organizationUserId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Test company",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = organizationUserId,
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = permission,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        });
        return (organizationId, organizationUserId, applicationUserId);
    }

    private static CSweet.Domain.WorkManagement.WorkBoard Board(
        Guid organizationId,
        string name) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
