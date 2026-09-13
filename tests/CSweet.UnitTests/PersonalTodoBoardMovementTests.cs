using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class PersonalTodoBoardMovementTests
{
    [Fact]
    public async Task OwnerCanMoveBackToBacklogWithRevisionAndOwnershipChecks()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = new Organization { Id = Guid.NewGuid(), Name = "Example", Status = OrganizationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Manager",
            EmployeeType = EmployeeType.Human, IsActive = true, ApplicationUserId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, IsActive = true, ApplicationUserId = Guid.NewGuid(), ReportsToOrganizationUserId = manager.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.AddRange(org, manager, owner); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(owner.Id, null);
        var item = await service.AddAsync(org.Id, actor, new("Test task", "Details", "Medium", null, "create"));
        item = await service.SetHumanStatusAsync(org.Id, actor, new(item.Id, "Completed", item.Revision, "Finished", null, "done"));
        var oldRevision = item.Revision;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetHumanStatusAsync(org.Id,
            new PersonalTodoActor(manager.Id, null), new(item.Id, "Backlog", item.Revision, null, null, "manager-move")));
        item = await service.SetHumanStatusAsync(org.Id, actor, new(item.Id, "Backlog", item.Revision, null, null, "backlog"));
        Assert.Equal("Backlog", item.Status); Assert.Null(item.ResultSummary); Assert.Null(item.BlockReason);
        var task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(WorkBoardColumnCategory.ToDo, (await db.WorkBoardColumns.SingleAsync(x => x.Id == task.BoardColumnId)).Category);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SetHumanStatusAsync(org.Id, actor,
            new(item.Id, "Ready", oldRevision, null, null, "stale")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetHumanStatusAsync(org.Id, actor,
            new(item.Id, "Blocked", item.Revision, null, "", "no-reason")));
        await service.ArchiveAsync(org.Id, actor, new(item.Id, item.Revision, "archive"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetHumanStatusAsync(org.Id, actor,
            new(item.Id, "Ready", task.Revision, null, null, "archived-move")));
    }
}
