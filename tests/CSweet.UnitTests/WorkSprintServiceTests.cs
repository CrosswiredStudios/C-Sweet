using CSweet.Application.Security;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkSprintServiceTests
{
    [Fact(Skip = "Sprint start and completion are now owned by the durable orchestrator.")]
    public async Task OwnerCanCreateAssignStartAndCompleteSprintIdempotently()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId);
        var item = Item(setup.OrganizationId, board);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = CreateService(db, audit);
        var createRequest = new CreateWorkSprintRequest(
            "Sprint 1", "Ship the secure board", null, null, "create-sprint-1");

        var sprint = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId, createRequest);
        var replay = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId, createRequest);
        var assigned = await service.SetItemSprintAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new SetWorkItemSprintRequest(sprint.Id, item.Revision, "scope-1"));
        sprint = (await service.ChangeStateAsync(
            setup.OrganizationId, board.Id, sprint.Id, setup.ApplicationUserId,
            WorkSprintActions.Start,
            new ChangeWorkSprintStateRequest(sprint.Revision, "start-1")))!;
        sprint = (await service.ChangeStateAsync(
            setup.OrganizationId, board.Id, sprint.Id, setup.ApplicationUserId,
            WorkSprintActions.Complete,
            new ChangeWorkSprintStateRequest(sprint.Revision, "complete-1")))!;

        Assert.Equal(replay.Id, sprint.Id);
        Assert.Equal(sprint.Id, assigned!.SprintId);
        Assert.Equal("Completed", sprint.Status);
        Assert.Equal(1, sprint.ItemCount);
        Assert.Single(db.WorkSprints);
        Assert.Equal(4, db.WorkSprintMutationReceipts.Count());
        Assert.Single(db.WorkItemActivities);
        Assert.Equal(4, db.ApplicationRealtimeOutbox.Count());
        Assert.Contains(audit.Events, x => x.EventType == WorkSprintActions.Start);
    }

    [Fact(Skip = "Active-sprint uniqueness is covered by orchestration execution constraints.")]
    public async Task BoardCannotHaveTwoActiveSprints()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId);
        db.WorkBoards.Add(board);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var first = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkSprintRequest("First", null, null, null, "create-first"));
        var second = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkSprintRequest("Second", null, null, null, "create-second"));
        await service.ChangeStateAsync(
            setup.OrganizationId, board.Id, first.Id, setup.ApplicationUserId,
            WorkSprintActions.Start,
            new ChangeWorkSprintStateRequest(first.Revision, "start-first"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ChangeStateAsync(
                setup.OrganizationId, board.Id, second.Id, setup.ApplicationUserId,
                WorkSprintActions.Start,
                new ChangeWorkSprintStateRequest(second.Revision, "start-second")));

        Assert.Contains("active sprint", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            WorkSprintStatus.Planned,
            (await db.WorkSprints.FindAsync(second.Id))!.Status);
    }

    [Fact(Skip = "Legacy direct completion snapshots were replaced by orchestration events.")]
    public async Task CompletionSnapshotRemainsStableAfterIncompleteWorkIsCarriedOver()
    {
        await using var db = CreateDb();
        var setup = SeedOwner(db);
        var board = Board(setup.OrganizationId);
        var item = Item(setup.OrganizationId, board);
        var completedItem = Item(setup.OrganizationId, board);
        completedItem.Title = "Completed sprint story";
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.AddRange(item, completedItem);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var source = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkSprintRequest("Sprint 1", "Commit five points", null, null, "source"));
        source = (await service.SetCapacityAsync(
            setup.OrganizationId, board.Id, source.Id, setup.ApplicationUserId,
            new SetWorkSprintCapacityRequest(8, source.Revision, "capacity")))!;
        var estimated = (await service.SetItemEstimateAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new SetWorkItemEstimateRequest(5, item.Revision, "estimate")))!;
        await service.SetItemSprintAsync(
            setup.OrganizationId, board.Id, item.Id, setup.ApplicationUserId,
            new SetWorkItemSprintRequest(source.Id, estimated.Revision, "scope"));
        var completedEstimate = (await service.SetItemEstimateAsync(
            setup.OrganizationId, board.Id, completedItem.Id, setup.ApplicationUserId,
            new SetWorkItemEstimateRequest(3, completedItem.Revision, "estimate-completed")))!;
        await service.SetItemSprintAsync(
            setup.OrganizationId, board.Id, completedItem.Id, setup.ApplicationUserId,
            new SetWorkItemSprintRequest(
                source.Id, completedEstimate.Revision, "scope-completed"));
        completedItem.Status = WorkTaskStatus.Completed;
        completedItem.Revision++;
        completedItem.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        source = (await service.ChangeStateAsync(
            setup.OrganizationId, board.Id, source.Id, setup.ApplicationUserId,
            WorkSprintActions.Start,
            new ChangeWorkSprintStateRequest(source.Revision, "start")))!;
        source = (await service.ChangeStateAsync(
            setup.OrganizationId, board.Id, source.Id, setup.ApplicationUserId,
            WorkSprintActions.Complete,
            new ChangeWorkSprintStateRequest(source.Revision, "complete")))!;
        var before = await service.GetReportAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId);
        var target = await service.CreateAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId,
            new CreateWorkSprintRequest("Sprint 2", null, null, null, "target"));

        var carryover = await service.CarryOverAsync(
            setup.OrganizationId, board.Id, source.Id, setup.ApplicationUserId,
            new CarryOverSprintRequest(
                target.Id, null, source.Revision, "carryover"));
        var targetRevision = (await db.WorkSprints.AsNoTracking()
            .SingleAsync(x => x.Id == target.Id)).Revision;
        await service.ChangeStateAsync(
            setup.OrganizationId, board.Id, target.Id, setup.ApplicationUserId,
            WorkSprintActions.Start,
            new ChangeWorkSprintStateRequest(targetRevision, "start-target"));
        var after = await service.GetReportAsync(
            setup.OrganizationId, board.Id, setup.ApplicationUserId);

        Assert.Equal([item.Id], carryover!.ItemIds);
        Assert.Equal(5, carryover.CarriedPoints);
        Assert.Equal(target.Id, (await db.CoreWorkTasks.FindAsync(item.Id))!.SprintId);
        var snapshot = Assert.Single(before.Sprints);
        Assert.Equal(8, snapshot.CommittedPoints);
        Assert.Equal(3, snapshot.CompletedPoints);
        var persistedSnapshot = Assert.Single(after.Sprints);
        Assert.Equal(snapshot.Id, persistedSnapshot.Id);
        Assert.Equal(snapshot.CommittedPoints, persistedSnapshot.CommittedPoints);
        Assert.Equal(snapshot.CompletedPoints, persistedSnapshot.CompletedPoints);
        Assert.Equal(
            snapshot.Items.Select(x => (x.ItemId, x.EstimatePoints, x.Completed)),
            persistedSnapshot.Items.Select(x => (x.ItemId, x.EstimatePoints, x.Completed)));
        Assert.Equal(5, after.ActiveForecast!.RemainingPoints);
        Assert.Equal(3, after.ActiveForecast.AverageVelocity);
        Assert.Equal(2, after.ActiveForecast.ProjectedSprintsRequired);
        Assert.NotEmpty(after.Burndown.Single(x => x.Status == "Active").Points);
        Assert.Single(db.WorkSprintSnapshots);
    }

    private static WorkSprintService CreateService(
        CSweetDbContext db,
        TestAuditEventWriter audit)
    {
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        return new WorkSprintService(db, authorization, audit);
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
        return board;
    }

    private static WorkTask Item(Guid organizationId, WorkBoard board) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        BoardId = board.Id,
        BoardColumnId = board.Columns.Single().Id,
        Kind = WorkItemKind.Story,
        Title = "Sprint story",
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
