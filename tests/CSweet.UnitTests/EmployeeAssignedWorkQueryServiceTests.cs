using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class EmployeeAssignedWorkQueryServiceTests
{
    [Fact]
    public async Task ProjectionDeduplicatesRelationshipsExcludesPersonalBoardsAndProtectsBoardLinks()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var columnId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Org",
            Status = OrganizationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser { Id = managerId, OrganizationId = organizationId, DisplayName = "Manager",
                EmployeeType = EmployeeType.Human, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
            new OrganizationUser { Id = employeeId, OrganizationId = organizationId, DisplayName = "Agent",
                EmployeeType = EmployeeType.Agent, AgentInstallationId = installationId,
                ReportsToOrganizationUserId = managerId, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        var board = new WorkBoard { Id = boardId, OrganizationId = organizationId, Key = "TEAM",
            Name = "Team board", Kind = WorkBoardKind.Standard, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow };
        board.Columns.Add(new WorkBoardColumn { Id = columnId, BoardId = boardId, Name = "Doing",
            Category = WorkBoardColumnCategory.InProgress, Position = 0 });
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = boardId,
            BoardColumnId = columnId, AssignedEmployeeId = employeeId,
            AccountableOrganizationUserId = employeeId, Title = "Canonical task", Description = "",
            Status = WorkTaskStatus.Running, Priority = WorkTaskPriority.High,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        item.StageAssignments.Add(new WorkItemStageAssignment { Id = Guid.NewGuid(),
            OrganizationId = organizationId, BoardId = boardId, WorkItemId = item.Id, StageKey = "review",
            OrganizationUserId = employeeId, CreatedAt = DateTimeOffset.UtcNow });
        item.StageAssignments.Add(new WorkItemStageAssignment { Id = Guid.NewGuid(),
            OrganizationId = organizationId, BoardId = boardId, WorkItemId = item.Id, StageKey = "delivery",
            AgentInstallationId = installationId, CreatedAt = DateTimeOffset.UtcNow });
        var personalBoard = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = organizationId,
            Key = "PERSONAL", Name = "Personal", Kind = WorkBoardKind.Personal,
            OwnerOrganizationUserId = employeeId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var personalColumn = new WorkBoardColumn { Id = Guid.NewGuid(), BoardId = personalBoard.Id,
            Name = "To Do", Category = WorkBoardColumnCategory.ToDo, Position = 0 };
        personalBoard.Columns.Add(personalColumn);
        db.AddRange(board, item, personalBoard, new WorkTask { Id = Guid.NewGuid(), OrganizationId = organizationId,
            BoardId = personalBoard.Id, BoardColumnId = personalColumn.Id, AssignedEmployeeId = employeeId,
            Title = "Private queue item", Description = "", Status = WorkTaskStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var service = new EmployeeAssignedWorkQueryService(db, TimeProvider.System);
        var result = await service.GetAsync(organizationId, employeeId, managerId);
        var projected = Assert.Single(result.Items);
        Assert.Equal(item.Id, projected.Item.Id);
        Assert.Equal([Wire.WorkAssignmentRelationships.DirectAssignee,
            Wire.WorkAssignmentRelationships.AccountableOwner,
            Wire.WorkAssignmentRelationships.StageAssignee,
            Wire.WorkAssignmentRelationships.StageAgent], projected.Relationships);
        Assert.False(projected.CanOpenBoard);

        db.ScopedActionGrants.Add(new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = organizationId,
            SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = managerId,
            Action = WorkBoardActions.Read, ScopeKind = GrantScopeKind.Board, ScopeId = boardId,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser, GrantedBySubjectId = managerId,
            GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        projected = Assert.Single((await service.GetAsync(organizationId, employeeId, managerId)).Items);
        Assert.True(projected.CanOpenBoard);
    }
}
