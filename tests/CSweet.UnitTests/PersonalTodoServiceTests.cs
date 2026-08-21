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
        Assert.Equal(WorkBoardKind.Personal, board.Kind);
        Assert.Equal(setup.Agent.Id, board.OwnerOrganizationUserId);
        Assert.Equal(
            [WorkBoardColumnCategory.ToDo, WorkBoardColumnCategory.InProgress,
             WorkBoardColumnCategory.Blocked, WorkBoardColumnCategory.Done],
            board.Columns.OrderBy(x => x.Position).Select(x => x.Category).ToArray());
        Assert.Equal(PersonalTodoActions.All.Count, ActiveGrants(db, board.Id, GrantSubjectKind.AgentInstallation,
            setup.Agent.AgentInstallationId!.Value).Count());
        Assert.Equal(5, ActiveGrants(db, board.Id, GrantSubjectKind.OrganizationUser,
            setup.FirstManager.Id).Count());

        setup.Agent.ReportsToOrganizationUserId = setup.SecondManager.Id;
        await db.SaveChangesAsync();
        await service.EnsureBoardAsync(setup.Organization.Id, setup.Agent.Id);

        Assert.Empty(ActiveGrants(db, board.Id, GrantSubjectKind.OrganizationUser,
            setup.FirstManager.Id));
        Assert.Equal(5, db.ScopedActionGrants.Count(x => x.ScopeId == board.Id &&
            x.SubjectId == setup.FirstManager.Id && x.RevokedAt != null));
        Assert.Equal(5, ActiveGrants(db, board.Id, GrantSubjectKind.OrganizationUser,
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
        var longTitle = new string('T', 200);
        var second = await service.AddAsync(setup.Organization.Id, manager,
            Add(longTitle, "second", setup.Agent.Id));
        Assert.True(first.Rank < second.Rank);

        second = await service.ReorderAsync(setup.Organization.Id, manager,
            new Wire.ReorderPersonalTodoItemRequest(second.Id, first.Id, second.Revision, "reorder"));
        var directory = await service.ListAsync(setup.Organization.Id, manager);
        Assert.Equal([second.Id, first.Id], directory.Boards.Single(x =>
            x.OwnerOrganizationUserId == setup.Agent.Id).Items.Select(x => x.Id).ToArray());

        var claimEventId = Guid.NewGuid();
        var claimed = await db.CoreWorkTasks.SingleAsync(x => x.Id == second.Id);
        var doing = await db.WorkBoardColumns.SingleAsync(x => x.BoardId == claimed.BoardId &&
            x.Category == WorkBoardColumnCategory.InProgress);
        claimed.Status = WorkTaskStatus.Running;
        claimed.BoardColumnId = doing.Id;
        claimed.ClaimEventId = claimEventId;
        claimed.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        claimed.Revision++;
        await db.SaveChangesAsync();

        var fullBlockReason = "The requested authority is not granted.\n\n" + new string('R', 300);
        var blocked = await service.BlockAsync(setup.Organization.Id, owner,
            new Wire.BlockPersonalTodoItemRequest(second.Id, claimEventId, claimed.Revision,
                fullBlockReason, "block"));
        Assert.Equal(WorkTaskStatus.Blocked.ToString(), blocked.Status);
        Assert.Equal(fullBlockReason, blocked.BlockReason);
        var notification = Assert.Single(await db.UserNotifications.Where(x =>
            x.RecipientOrganizationUserId == setup.FirstManager.Id &&
            x.Category == "PersonalTodoBlocked").ToListAsync());
        Assert.Equal("Personal task blocked", notification.Title);
        Assert.True(notification.Body.Length <= 219);
        Assert.StartsWith(new string('T', 40), notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', notification.Body);
        Assert.Equal(
            $"/organizations/{setup.Organization.Id:D}/employees/{setup.Agent.Id:D}",
            notification.ActionUri);

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
    public async Task AgentCanKeepClaimedWorkInDoingUntilAnExternalEventResumesItForCompletion()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var owner = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var item = await service.AddAsync(setup.Organization.Id, owner,
            Add("Hire Product Manager", "hire-product-manager", null));
        var firstEventId = Guid.NewGuid();
        var stored = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        var doingColumn = await db.WorkBoardColumns.SingleAsync(x =>
            x.BoardId == stored.BoardId && x.Category == WorkBoardColumnCategory.InProgress);
        stored.Status = WorkTaskStatus.Running;
        stored.BoardColumnId = doingColumn.Id;
        stored.ClaimEventId = firstEventId;
        stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        stored.Revision++;
        await db.SaveChangesAsync();

        var doing = await service.ReleaseAsync(setup.Organization.Id, owner,
            new Wire.ReleasePersonalTodoItemRequest(
                item.Id, firstEventId, stored.Revision, "wait-for-hire")
            {
                KeepInProgress = true
            });

        Assert.Equal(Wire.PersonalTodoStatuses.Running, doing.Status);
        var storedDoing = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Null(storedDoing.ClaimEventId);
        Assert.Null(storedDoing.ClaimExpiresAt);

        var resumed = await service.RequeueAsync(setup.Organization.Id, owner,
            new Wire.RequeuePersonalTodoItemRequest(doing.Id, doing.Revision, "hire-fulfilled"));
        Assert.Equal(Wire.PersonalTodoStatuses.Ready, resumed.Status);
        var completionEventId = Guid.NewGuid();
        storedDoing.Status = WorkTaskStatus.Running;
        storedDoing.BoardColumnId = doingColumn.Id;
        storedDoing.ClaimEventId = completionEventId;
        storedDoing.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        storedDoing.Revision++;
        await db.SaveChangesAsync();
        var completed = await service.CompleteAsync(setup.Organization.Id, owner,
            new Wire.CompletePersonalTodoItemRequest(
                item.Id, completionEventId, storedDoing.Revision, "Hire fulfilled", "complete-hire"));

        Assert.Equal(Wire.PersonalTodoStatuses.Completed, completed.Status);
    }

    [Fact]
    public async Task AgentCanDeferClaimedWorkAndReconciliationWakesItOnceWhenDue()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var owner = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var item = await service.AddAsync(setup.Organization.Id, owner,
            Add("Await architecture", "await-architecture", null));
        var eventId = Guid.NewGuid();
        var stored = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        var doing = await db.WorkBoardColumns.SingleAsync(x =>
            x.BoardId == stored.BoardId && x.Category == WorkBoardColumnCategory.InProgress);
        stored.Status = WorkTaskStatus.Running;
        stored.BoardColumnId = doing.Id;
        stored.ClaimEventId = eventId;
        stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        stored.Revision++;
        await db.SaveChangesAsync();

        var deferred = await service.DeferAsync(setup.Organization.Id, owner,
            new Wire.DeferPersonalTodoItemRequest(
                item.Id, eventId, stored.Revision,
                DateTimeOffset.UtcNow.AddMinutes(30),
                "Waiting for the Architect's next turn.",
                setup.FirstManager.Id,
                "defer-architecture"));

        Assert.Equal(Wire.PersonalTodoStatuses.Running, deferred.Status);
        Assert.NotNull(deferred.Wait);
        Assert.Equal(setup.FirstManager.Id, deferred.Wait!.WaitingOnOrganizationUserId);
        stored = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        stored.NextReviewAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        await service.ReconcileAsync();

        var ready = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(WorkTaskStatus.Ready, ready.Status);
        Assert.Null(ready.NextReviewAt);
        Assert.Null(ready.WaitingReason);
        Assert.Null(ready.WaitingOnOrganizationUserId);
        Assert.Equal(2, await db.AgentPlatformEventOutbox.CountAsync(x =>
            x.EventType == Wire.PersonalTodoEvents.Available));
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

    [Fact]
    public async Task BacklogWorkDoesNotDispatchUntilExplicitlyActivated()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var owner = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);

        var item = await service.AddAsync(
            setup.Organization.Id,
            owner,
            Add("Hire QA", "hire-qa", null) with { StartInBacklog = true });

        Assert.Equal(Wire.PersonalTodoStatuses.Backlog, item.Status);
        Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());

        item = await service.ActivateAsync(
            setup.Organization.Id,
            owner,
            new Wire.ActivatePersonalTodoItemRequest(item.Id, item.Revision, "activate-hire-qa"));

        Assert.Equal(Wire.PersonalTodoStatuses.Ready, item.Status);
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == Wire.PersonalTodoEvents.Available).ToListAsync());
    }

    [Fact]
    public async Task ReconcileQueuesAReplacementWakeForStrandedReadyAgentWork()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        await service.AddAsync(setup.Organization.Id,
            new PersonalTodoActor(setup.FirstManager.Id, null),
            Add("Stranded task", "stranded", setup.Agent.Id));
        var original = await db.AgentPlatformEventOutbox.SingleAsync();
        original.Status = AgentPlatformEventOutboxStatus.Published;
        original.OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        original.PublishedAt = original.OccurredAt;
        await db.SaveChangesAsync();

        await service.ReconcileAsync();

        Assert.Equal(2, await db.AgentPlatformEventOutbox.CountAsync(x =>
            x.EventType == Wire.PersonalTodoEvents.Available));
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.Status == AgentPlatformEventOutboxStatus.Pending).ToListAsync());
    }

    [Fact]
    public async Task TicketMentionsAreValidatedPersistedAndReturnedAsAuthoritativeIdentities()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var manager = new PersonalTodoActor(setup.FirstManager.Id, null);
        var request = Add("Tell @Morgan a joke", "mentioned", setup.Agent.Id) with
        {
            Mentions =
            [
                new Wire.WorkItemMentionInput(setup.FirstManager.Id,
                    Wire.WorkItemMentionFields.Title, 5, 7)
            ]
        };

        var item = await service.AddAsync(setup.Organization.Id, manager, request);

        Assert.Equal(setup.FirstManager.Id, Assert.Single(item.Mentions).OrganizationUserId);
        var span = Assert.Single(item.MentionSpans);
        Assert.Equal("@Morgan", span.DisplayText);
        Assert.Equal(Wire.WorkItemMentionFields.Title, span.Field);
        Assert.Equal("@Morgan", item.Title.Substring(span.Offset, span.Length));

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(
            setup.Organization.Id, manager,
            request with
            {
                IdempotencyKey = "malformed",
                Mentions =
                [
                    new Wire.WorkItemMentionInput(setup.SecondManager.Id,
                        Wire.WorkItemMentionFields.Title, 5, 7)
                ]
            }));
    }

    [Fact]
    public async Task HumanOwnerCanCreateTransitionEditAndArchiveWithoutASecondTaskEngine()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        setup.FirstManager.ApplicationUserId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.FirstManager.Id, null);

        var item = await service.AddAsync(setup.Organization.Id, actor,
            Add("Review roadmap", "human-create", null));
        item = await service.SetHumanStatusAsync(setup.Organization.Id, actor,
            new(item.Id, Wire.PersonalTodoStatuses.Running, item.Revision, null, null, "doing"));
        Assert.Equal(Wire.PersonalTodoStatuses.Running, item.Status);

        item = await service.UpdateAsync(setup.Organization.Id, actor,
            new(item.Id, "Review product roadmap", "Prepare decisions", Wire.WorkPriorities.High,
                null, item.Revision, "edit"));
        Assert.Equal("Review product roadmap", item.Title);

        item = await service.SetHumanStatusAsync(setup.Organization.Id, actor,
            new(item.Id, Wire.PersonalTodoStatuses.Blocked, item.Revision, null,
                "Waiting for finance.", "block"));
        Assert.Equal("Waiting for finance.", item.BlockReason);

        item = await service.ArchiveAsync(setup.Organization.Id, actor,
            new(item.Id, item.Revision, "archive"));
        Assert.Empty((await service.ListAsync(setup.Organization.Id, actor)).Boards.Single(x =>
            x.OwnerOrganizationUserId == setup.FirstManager.Id).Items);
        Assert.NotNull((await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id)).ArchivedAt);
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
