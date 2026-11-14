using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
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
        var reader = new BlockedWorkAttentionReader(db, service, TimeProvider.System);
        var issue = Assert.Single(await reader.ReadAsync(setup.Organization.Id, manager));
        Assert.Equal(blocked.Id, issue.ItemId); Assert.True(issue.CanRetry);
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
