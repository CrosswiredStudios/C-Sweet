using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Fact]
    public async Task SharedPrerequisiteGroupsAffectedTasksAndIndependentBlockersStaySeparate()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var org = setup.Organization.Id;
        var actor = new PersonalTodoActor(setup.FirstManager.Id, null);
        var service = new PersonalTodoService(db, TimeProvider.System);
        var auth = new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(db);
        var cases = new WorkIssueService(db, auth, TimeProvider.System);
        var reader = new OverviewIssueReader(db, service, new(db, service, TimeProvider.System, new(db, service, auth, cases)),
            new CSweet.Infrastructure.Communications.ExecutiveDecisionService(db, new CSweet.Infrastructure.Core.ChatTurnService(db)),
            new(db, new(db, TimeProvider.System), TimeProvider.System), cases);
        async Task<Guid> Create(string title) => (await service.AddAsync(org, actor, Add(title, title, setup.Agent.Id))).Id;
        var source = await Create("Get specialist input");
        var second = await Create("Get the missing artwork");
        var firstTask = await Create("Prepare the layout");
        var otherTask = await Create("Prepare the playtest");
        foreach (var row in await db.CoreWorkTasks.Where(t => t.Id == firstTask || t.Id == otherTask).ToListAsync())
            row.Status = WorkTaskStatus.Blocked;
        db.WorkItemDependencies.AddRange(new CSweet.Domain.WorkManagement.WorkItemDependency { WorkItemId = firstTask, DependsOnWorkItemId = source },
            new() { WorkItemId = otherTask, DependsOnWorkItemId = source }, new() { WorkItemId = otherTask, DependsOnWorkItemId = second });
        await db.SaveChangesAsync();
        var issues = await reader.ReadAsync(org, actor, false, default);
        Assert.Empty(issues.UserActions);
        Assert.Equal(2, issues.Team.Count);
        var shared = Assert.Single(issues.Team, c => c.Key == "work:" + source.ToString("N"));
        Assert.Equal(new[] { firstTask, otherTask }.Order(), shared.Work.Select(w => w.ItemId).Order());
        Assert.Contains(shared.AffectedWork, w => w.ItemId == source);
        Assert.All(shared.Work, w => Assert.False(w.CanRetry));
        Assert.Equal(otherTask, Assert.Single(Assert.Single(issues.Team, c => c.Key == "work:" + second.ToString("N")).Work).ItemId);
    }

    [Fact]
    public async Task BlockedWorkAttentionUsesActualStateAndAuthorizedRetryThenClearsAfterRecovery()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var manager = new PersonalTodoActor(setup.FirstManager.Id, null);
        var blocked = await service.AddAsync(setup.Organization.Id, manager, Add("Review missing evidence", "attention-block", setup.Agent.Id));
        var waiting = await service.AddAsync(setup.Organization.Id, manager, Add("Await manager", "attention-wait", setup.Agent.Id));
        var archived = await service.AddAsync(setup.Organization.Id, manager, Add("Old blocker", "attention-archive", setup.Agent.Id));
        var blockedRow = await db.CoreWorkTasks.SingleAsync(x => x.Id == blocked.Id);
        blockedRow.Status = WorkTaskStatus.Blocked; blockedRow.BlockReason = "The required source evidence is unavailable.";
        var waitRow = await db.CoreWorkTasks.SingleAsync(x => x.Id == waiting.Id);
        waitRow.Status = WorkTaskStatus.Running; waitRow.WaitingReason = "Awaiting review"; waitRow.NextReviewAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var archiveRow = await db.CoreWorkTasks.SingleAsync(x => x.Id == archived.Id);
        archiveRow.Status = WorkTaskStatus.Blocked; archiveRow.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var auth = new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(db);
        var reader = new BlockedWorkAttentionReader(db, service, TimeProvider.System, new(db, service, auth, new(db, auth, TimeProvider.System)));
        var issue = Assert.Single(await reader.ReadAsync(setup.Organization.Id, manager));
        Assert.Equal(blocked.Id, issue.ItemId); Assert.False(issue.CanRetry);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => reader.RetryAsync(setup.Organization.Id, manager, new(issue.ItemId, issue.Revision, "unchanged"), default));
        Assert.Equal(blockedRow.BlockReason, issue.Reason);
        Assert.Contains($"item={blocked.Id:D}", issue.ActionUri);
        Assert.Empty(await reader.ReadAsync(setup.Organization.Id, new(setup.SecondManager.Id, null)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(Guid.NewGuid(), manager));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RequeueAsync(setup.Organization.Id, manager,
            new(issue.ItemId, issue.Revision + 1, "stale-attention-retry")));
        await service.RequeueAsync(setup.Organization.Id, manager, new(issue.ItemId, issue.Revision, "reviewed-attention-retry"));
        Assert.Empty(await reader.ReadAsync(setup.Organization.Id, manager));
        Assert.Contains(await db.AgentPlatformEventOutbox.ToListAsync(), x => x.EventType == Wire.PersonalTodoEvents.Available);
    }
}
