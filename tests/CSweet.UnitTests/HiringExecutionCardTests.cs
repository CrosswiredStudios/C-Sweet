using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Theory]
    [InlineData(WorkTaskStatus.Ready)]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.Blocked)]
    [InlineData(WorkTaskStatus.Backlog)]
    public async Task WithdrawalCancelsCorrelatedCardAndActivatesOneSuccessor(WorkTaskStatus initial)
    {
        await using var db = CreateDb();
        var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.FirstManager.Id, null);
        var plan = HiringPlan(setup.Organization.Id, setup.Agent.AgentInstallationId!.Value);
        var next = HiringPlan(plan.OrganizationId, plan.RequestingInstallationId);
        db.AddRange(plan, next); await db.SaveChangesAsync();
        var first = await service.AddAsync(plan.OrganizationId, actor,
            Add("Hire same title", "first-hiring", setup.Agent.Id) with { CorrelationId = $"hiring-recommendation:{plan.Id:N}" });
        var second = await service.AddAsync(plan.OrganizationId, actor,
            Add("Hire same title", "second-hiring", setup.Agent.Id) with { CorrelationId = $"hiring-recommendation:{next.Id:N}", StartInBacklog = true });
        var card = await db.CoreWorkTasks.SingleAsync(x => x.Id == first.Id);
        card.Status = initial; card.ClaimEventId = Guid.NewGuid(); card.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        card.NextReviewAt = DateTimeOffset.UtcNow.AddHours(1); card.WaitingReason = "Await hire";
        await db.SaveChangesAsync();
        var oldClaim = card.ClaimEventId.Value; var oldRevision = card.Revision;
        var hiring = new HiringService(db, null!, new TestAuditEventWriter());
        await hiring.WithdrawRecommendationAsync(plan.OrganizationId, plan.RequestingInstallationId, new(plan.Id, "Owner replaced role", "withdraw"));
        db.ChangeTracker.Clear();
        card = await db.CoreWorkTasks.SingleAsync(x => x.Id == first.Id);
        Assert.Equal(WorkTaskStatus.Cancelled, card.Status);
        Assert.Null(card.ClaimEventId); Assert.Null(card.ClaimExpiresAt); Assert.Null(card.NextReviewAt); Assert.Null(card.WaitingReason);
        Assert.True(card.Revision > oldRevision);
        Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.SingleAsync(x => x.Id == second.Id)).Status);
        Assert.Single(await db.WorkItemActivities.Where(x => x.WorkItemId == first.Id && x.Action == "hiring.source-settled").ToListAsync());
        var revision = card.Revision;
        var outboxCount = await db.ApplicationRealtimeOutbox.CountAsync();
        await hiring.WithdrawRecommendationAsync(plan.OrganizationId, plan.RequestingInstallationId, new(plan.Id, "Repeat", "withdraw-again"));
        Assert.Equal(revision, (await db.CoreWorkTasks.SingleAsync(x => x.Id == first.Id)).Revision);
        Assert.Equal(outboxCount, await db.ApplicationRealtimeOutbox.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync(plan.OrganizationId,
            new(setup.Agent.Id, plan.RequestingInstallationId), new(first.Id, oldClaim, oldRevision, "Late completion", "late")));
        Assert.Equal(WorkTaskStatus.Cancelled, (await db.CoreWorkTasks.SingleAsync(x => x.Id == first.Id)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalSourceRepairsLastOrphanEvenWithEmptyBacklog(bool fulfilled)
    {
        await using var db = CreateDb();
        var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var plan = HiringPlan(setup.Organization.Id, setup.Agent.AgentInstallationId!.Value);
        db.Add(plan); await db.SaveChangesAsync();
        var item = await service.AddAsync(plan.OrganizationId, new(setup.FirstManager.Id, null),
            Add("Legacy orphan", "orphan-hiring", setup.Agent.Id) with { CorrelationId = $"hiring-recommendation:{plan.Id:N}" });
        plan.Status = fulfilled ? ProposalStatus.Approved : ProposalStatus.Cancelled;
        plan.FulfilledHeadcount = fulfilled ? plan.Headcount : 0;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var hiring = new HiringService(db, null!, new TestAuditEventWriter());
        Assert.Empty(await hiring.ListRecommendationsForInstallationAsync(plan.OrganizationId, plan.RequestingInstallationId));
        var card = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(fulfilled ? WorkTaskStatus.Completed : WorkTaskStatus.Cancelled, card.Status);
        var revision = card.Revision;
        await hiring.ListRecommendationsForInstallationAsync(plan.OrganizationId, plan.RequestingInstallationId);
        Assert.Equal(revision, (await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id)).Revision);
    }

    [Fact]
    public async Task WithdrawalDoesNotSettleAnotherInstallationsCard()
    {
        await using var db = CreateDb();
        var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var plan = HiringPlan(setup.Organization.Id, setup.Agent.AgentInstallationId!.Value);
        db.Add(plan); await db.SaveChangesAsync();
        var unrelated = await service.AddAsync(plan.OrganizationId, new(setup.SecondManager.Id, null),
            Add("Other installation", "other-hiring", setup.UnrelatedAgent.Id) with { CorrelationId = $"hiring-recommendation:{plan.Id:N}" });
        var hiring = new HiringService(db, null!, new TestAuditEventWriter());
        await hiring.WithdrawRecommendationAsync(plan.OrganizationId, plan.RequestingInstallationId, new(plan.Id, "Withdraw", "withdraw"));
        Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.SingleAsync(x => x.Id == unrelated.Id)).Status);
    }

    [Fact]
    public async Task FulfillmentCompletesExecutionCardOnlyAfterLastSeat()
    {
        await using var db = CreateDb();
        var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var plan = HiringPlan(setup.Organization.Id, setup.Agent.AgentInstallationId!.Value);
        plan.Headcount = 2; db.Add(plan); await db.SaveChangesAsync();
        var item = await service.AddAsync(plan.OrganizationId, new(setup.FirstManager.Id, null),
            Add("Hire two", "multi-hiring", setup.Agent.Id) with { CorrelationId = $"hiring-recommendation:{plan.Id:N}" });
        var hiring = new HiringService(db, null!, new TestAuditEventWriter());
        await hiring.ResolveRecommendationAsync(plan.OrganizationId, plan.RequestingInstallationId, new(plan.Id, setup.FirstManager.Id, "first-seat"));
        Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id)).Status);
        await hiring.ResolveRecommendationAsync(plan.OrganizationId, plan.RequestingInstallationId, new(plan.Id, setup.SecondManager.Id, "second-seat"));
        Assert.Equal(WorkTaskStatus.Completed, (await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id)).Status);
    }

    private static WorkforcePlan HiringPlan(Guid organization, Guid installation) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organization, RequestingInstallationId = installation,
        Title = "Product Manager", Status = ProposalStatus.Pending, Headcount = 1,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, IdempotencyKey = Guid.NewGuid().ToString("N")
    };
}
