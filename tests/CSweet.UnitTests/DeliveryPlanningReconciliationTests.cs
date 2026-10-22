using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class DeliveryPlanningReconciliationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RepeatedStalePlanningPulseDoesNotInvalidateManagerControlRevision()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());
        f.Task.PlanningRevision++;
        await f.Db.SaveChangesAsync();
        await f.Service.PulseAsync();
        var stopped = await f.Read();
        Assert.All(stopped.Executions, e => Assert.Equal("Blocked", e.Status));
        var revisions = f.Db.WorkDeliveryExecutions.ToDictionary(e => e.Id, e => e.Revision);
        await f.Service.PulseAsync();
        Assert.Equal(stopped.Revision, (await f.Read()).Revision);
        Assert.All(f.Db.WorkDeliveryExecutions, e => Assert.Equal(revisions[e.Id], e.Revision));
        var paused = await f.Service.ControlAsync(f.Org, f.Manager.Id,
            new(stopped.Id, stopped.Revision, "pause", "pause-stable-blocker"));
        Assert.Equal("Paused", paused.Status);
    }

    [Fact]
    public async Task ExplicitReconciliationRepinsCanonicalCriteriaAndRequiresFreshAcceptance()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());
        var planning = JsonSerializer.Deserialize<WorkItemPlanningSpecification>(f.Task.PlanningSpecificationJson!, Json)!;
        f.Task.PlanningSpecificationJson = JsonSerializer.Serialize(planning with { AcceptanceCriteria = ["Works in Chromium"] }, Json);
        f.Task.PlanningRevision++;
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>(f.Task.DeliverySpecificationJson!, Json)!;
        f.Task.DeliverySpecificationJson = JsonSerializer.Serialize(delivery with { AcceptanceCriteria = ["Works in Chromium"] }, Json);
        await f.Db.SaveChangesAsync();
        await f.Service.PulseAsync();
        Assert.All((await f.Read()).Executions, e => Assert.Equal("Blocked", e.Status));
        var before = await f.Read();
        var paused = await f.Service.ControlAsync(f.Org, f.Manager.Id, new(before.Id, before.Revision, "pause", "pause-amendment"));
        var command = new ControlWorkDeliveryPlanRequest(paused.Id, paused.Revision, "reconcile-planning", "reconcile-amendment");
        var amended = await f.Service.ControlAsync(f.Org, f.Manager.Id, command);
        Assert.Equal(paused.ScopeRevision + 1, amended.ScopeRevision);
        Assert.Equal("Paused", amended.Status);
        Assert.All(amended.Executions.Where(e => before.Executions.Any(old => old.Id == e.Id)), e => Assert.Equal("Superseded", e.Status));
        Assert.All(amended.Executions.Where(e => !before.Executions.Any(old => old.Id == e.Id)), e => {
            Assert.Empty(e.Stages); Assert.Null(e.Candidate);
            Assert.Null(f.Db.WorkDeliveryExecutions.Single(row => row.Id == e.Id).AcceptanceJson);
        });
        Assert.Contains(amended.Scopes, s => s.ChildPlanningRevisions.GetValueOrDefault(f.Task.Id) == f.Task.PlanningRevision);
        Assert.Equal(amended.ScopeRevision, (await f.Service.ControlAsync(f.Org, f.Manager.Id, command)).ScopeRevision);
        await f.Service.ControlAsync(f.Org, f.Manager.Id, new(amended.Id, amended.Revision, "resume", "resume-amendment"));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("structure")]
    [InlineData("writer")]
    [InlineData("authority")]
    public async Task ReconciliationRejectsUnsafeOrUnapprovedChanges(string failure)
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        var plan = await f.Activate(await f.Configure());
        if (failure != "active") plan = await f.Service.ControlAsync(f.Org, f.Manager.Id, new(plan.Id, plan.Revision, "pause", "pause"));
        if (failure == "structure")
        {
            var ticket = f.Db.CoreWorkTasks.Single(t => t.Id == plan.Scopes[0].TicketWorkflow!.TicketId);
            var planning = JsonSerializer.Deserialize<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!, Json)!;
            ticket.PlanningSpecificationJson = JsonSerializer.Serialize(planning with { ArchitectureArtifactDigest = "changed-identity" }, Json);
        }
        if (failure == "writer")
        {
            var execution = f.Db.WorkDeliveryExecutions.First(e => e.PlanId == plan.Id);
            var stage = new WorkStageExecution { Id = Guid.NewGuid(), DeliveryExecutionId = execution.Id, StageKey = "quality", Status = WorkStageExecutionStatus.Running,
                Attempts = [new() { Id = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Running }] };
            execution.Stages.Add(stage); f.Db.WorkStageExecutions.Add(stage);
        }
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.ControlAsync(f.Org,
            failure == "authority" ? f.Author.Id : f.Manager.Id,
            new(plan.Id, plan.Revision, "reconcile-planning", "unsafe-reconcile")));
        Assert.Equal(plan.ScopeRevision, (await f.Read()).ScopeRevision);
    }
}
