using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class PersonalTodoServiceTests
{
    [Fact]
    public async Task ProvisioningIsIdempotentAndRotatesDirectManagerGrants()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);

        await service.EnsureBoardAsync(setup.Organization.Id, setup.Agent.Id);
        await service.EnsureBoardAsync(setup.Organization.Id, setup.Agent.Id);

        var board = Assert.Single(await db.WorkBoards.Include(x => x.Columns).ToListAsync());
        Assert.True(board.IsPersonalTodo);
        Assert.Equal(setup.Agent.Id, board.PersonalTodoOwnerOrganizationUserId);
        Assert.Equal(
            [WorkBoardColumnCategory.ToDo, WorkBoardColumnCategory.InProgress, WorkBoardColumnCategory.Done],
            board.Columns.OrderBy(x => x.Position).Select(x => x.Category).ToArray());
        Assert.Equal(7, ActiveGrants(db, board.Id, GrantSubjectKind.AgentInstallation,
            setup.Agent.AgentInstallationId!.Value).Count());
        Assert.Equal(4, ActiveGrants(db, board.Id, GrantSubjectKind.OrganizationUser,
            setup.FirstManager.Id).Count());

        setup.Agent.ReportsToOrganizationUserId = setup.SecondManager.Id;
        await db.SaveChangesAsync();
        await service.EnsureBoardAsync(setup.Organization.Id, setup.Agent.Id);

        Assert.Empty(ActiveGrants(db, board.Id, GrantSubjectKind.OrganizationUser,
            setup.FirstManager.Id));
        Assert.Equal(4, db.ScopedActionGrants.Count(x => x.ScopeId == board.Id &&
            x.SubjectId == setup.FirstManager.Id && x.RevokedAt != null));
        Assert.Equal(4, ActiveGrants(db, board.Id, GrantSubjectKind.OrganizationUser,
            setup.SecondManager.Id).Count());
    }

    [Fact]
    public async Task ManagerCanRankAndRequeueWhileOwnerPrivatelyBlocksClaimedWork()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var manager = new PersonalTodoActor(setup.FirstManager.Id, null);
        var owner = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);

        var first = await service.AddAsync(setup.Organization.Id, manager,
            Add("First", "first", setup.Agent.Id));
        var second = await service.AddAsync(setup.Organization.Id, manager,
            Add("Second", "second", setup.Agent.Id));
        Assert.True(first.Rank < second.Rank);

        second = await service.ReorderAsync(setup.Organization.Id, manager,
            new Wire.ReorderPersonalTodoItemRequest(second.Id, first.Id, second.Revision, "reorder"));
        var directory = await service.ListAsync(setup.Organization.Id, manager);
        Assert.Equal([second.Id, first.Id], Assert.Single(directory.Boards).Items.Select(x => x.Id).ToArray());

        var claimEventId = Guid.NewGuid();
        var claimed = await db.CoreWorkTasks.SingleAsync(x => x.Id == second.Id);
        var doing = await db.WorkBoardColumns.SingleAsync(x => x.BoardId == claimed.BoardId &&
            x.Category == WorkBoardColumnCategory.InProgress);
        claimed.Status = WorkTaskStatus.Running;
        claimed.BoardColumnId = doing.Id;
        claimed.PersonalTodoClaimEventId = claimEventId;
        claimed.PersonalTodoClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        claimed.Revision++;
        await db.SaveChangesAsync();

        var blocked = await service.BlockAsync(setup.Organization.Id, owner,
            new Wire.BlockPersonalTodoItemRequest(second.Id, claimEventId, claimed.Revision,
                "The requested authority is not granted.", "block"));
        Assert.Equal(WorkTaskStatus.Blocked.ToString(), blocked.Status);
        Assert.Equal("The requested authority is not granted.", blocked.BlockReason);
        Assert.Contains(await db.UserNotifications.ToListAsync(), x =>
            x.RecipientOrganizationUserId == setup.FirstManager.Id && x.Category == "PersonalTodoBlocked");

        var requeued = await service.RequeueAsync(setup.Organization.Id, manager,
            new Wire.RequeuePersonalTodoItemRequest(blocked.Id, blocked.Revision, "requeue"));
        Assert.Equal(WorkTaskStatus.Ready.ToString(), requeued.Status);
        Assert.Null(requeued.BlockReason);
        Assert.Equal(3, await db.AgentPlatformEventOutbox.CountAsync(x =>
            x.EventType == Wire.PersonalTodoEvents.Available));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddAsync(
            setup.Organization.Id, manager, Add("Not my report", "denied", setup.UnrelatedAgent.Id)));
    }

    [Fact]
    public async Task AddInfersTheOwningAgentAndIsIdempotent()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var owner = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var request = Add("Follow up", "same-key", null);

        var first = await service.AddAsync(setup.Organization.Id, owner, request);
        var replay = await service.AddAsync(setup.Organization.Id, owner, request);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(setup.Agent.Id, first.OwnerOrganizationUserId);
        Assert.Single(await db.CoreWorkTasks.ToListAsync());
    }

    private static Wire.AddPersonalTodoItemRequest Add(string title, string key, Guid? target) =>
        new(title, null, Wire.WorkPriorities.Medium, null, key, target);

    private static IQueryable<ScopedActionGrant> ActiveGrants(
        CSweetDbContext db, Guid boardId, GrantSubjectKind kind, Guid subjectId) =>
        db.ScopedActionGrants.Where(x => x.ScopeId == boardId && x.SubjectKind == kind &&
            x.SubjectId == subjectId && x.RevokedAt == null && PersonalTodoActions.All.Contains(x.Action));

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Setup Seed(CSweetDbContext db)
    {
        var organization = new Organization
        {
            Id = Guid.NewGuid(), Name = "Example", Status = OrganizationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var firstManager = User(organization.Id, "Morgan", EmployeeType.Human);
        var secondManager = User(organization.Id, "Riley", EmployeeType.Human);
        var agent = User(organization.Id, "Delivery Agent", EmployeeType.Agent);
        agent.AgentInstallationId = Guid.NewGuid();
        agent.ReportsToOrganizationUserId = firstManager.Id;
        var unrelatedAgent = User(organization.Id, "Unrelated Agent", EmployeeType.Agent);
        unrelatedAgent.AgentInstallationId = Guid.NewGuid();
        unrelatedAgent.ReportsToOrganizationUserId = secondManager.Id;
        db.AddRange(organization, firstManager, secondManager, agent, unrelatedAgent);
        return new Setup(organization, firstManager, secondManager, agent, unrelatedAgent);
    }

    private static OrganizationUser User(Guid organizationId, string name, EmployeeType type) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = name,
        EmployeeType = type, PermissionLevel = OrganizationPermissionLevel.Contributor,
        IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed record Setup(
        Organization Organization,
        OrganizationUser FirstManager,
        OrganizationUser SecondManager,
        OrganizationUser Agent,
        OrganizationUser UnrelatedAgent);
}
