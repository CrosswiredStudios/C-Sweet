using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Fact]
    public async Task PersonalPlansPreserveLongAuthoredTitlesDescriptionsCriteriaAndTaskEvidence()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var text = "Approved responsibility 🎮 " + new string('x', 100000);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add(text, "long-plan", null));
        var row = await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id);
        row.Status = WorkTaskStatus.Running; row.ClaimEventId = Guid.NewGuid(); row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        var original = Plan(root.Id);
        var request = original with { EpicTitle = text, Stories = original.Stories.Select(s => s with {
            Title = text, Description = text, AcceptanceCriteria = [text], Tasks = s.Tasks.Select(t => t with {
                Title = text, Description = text, AcceptanceCriteria = [text] }).ToArray() }).ToArray() };
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { PersonalTodoActions.CreatePlan }));
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, request);
        Assert.All(plan.Items, i => Assert.Equal(text, i.Title));
        Assert.All(plan.Items.Where(i => i.Id != root.Id), i => { Assert.Equal(text, i.Description); Assert.Equal(text, Assert.Single(i.AcceptanceCriteria)); });
        var task = plan.Items.First(i => i.Kind == "Task");
        var started = await service.ReportPlanTaskAsync(setup.Organization.Id, actor, new(root.Id, task.Id, task.Revision, "Running", null, "start-long"));
        await service.ReportPlanTaskAsync(setup.Organization.Id, actor, new(root.Id, task.Id, started.Revision, "Completed", text, "finish-long"));
        db.ChangeTracker.Clear();
        Assert.Equal(text, (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.Id)).ResultSummary);
    }

    [Theory]
    [InlineData("waiting", 6787)]
    [InlineData("waiting", 100000)]
    [InlineData("blocked", 100000)]
    [InlineData("completed", 100000)]
    public async Task AuthoredReportingTextPassesSchemaAndPersistsWithoutCeilings(string transition, int length)
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var owner = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var item = await service.AddAsync(setup.Organization.Id, owner, Add("Reconcile scope", "long-report", null));
        var row = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        var eventId = Guid.NewGuid(); row.Status = WorkTaskStatus.Running; row.ClaimEventId = eventId;
        row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5); await db.SaveChangesAsync();
        const string prefix = "Planning reconciliation remains with the responsible agents. ";
        var text = prefix + new string('x', length - prefix.Length);
        object request; string capability;
        if (transition == "waiting") { capability = PersonalTodoActions.Defer; request = new Wire.DeferPersonalTodoItemRequest(item.Id, eventId, row.Revision,
            DateTimeOffset.UtcNow.AddMinutes(5), text, setup.FirstManager.Id, "wait"); }
        else if (transition == "blocked") { capability = PersonalTodoActions.Block; request = new Wire.BlockPersonalTodoItemRequest(item.Id,eventId,row.Revision,text,"block"); }
        else { capability = PersonalTodoActions.Complete; request = new Wire.CompletePersonalTodoItemRequest(item.Id,eventId,row.Revision,text,"complete"); }
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        Wire.PersonalTodoItem result = request switch {
            Wire.DeferPersonalTodoItemRequest defer => await service.DeferAsync(setup.Organization.Id, owner, defer),
            Wire.BlockPersonalTodoItemRequest block => await service.BlockAsync(setup.Organization.Id, owner, block),
            Wire.CompletePersonalTodoItemRequest complete => await service.CompleteAsync(setup.Organization.Id, owner, complete), _ => throw new InvalidOperationException() };
        db.ChangeTracker.Clear(); row = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(text, transition == "waiting" ? row.WaitingReason : transition == "blocked" ? row.BlockReason : row.ResultSummary);
        Assert.Equal(text, transition == "waiting" ? result.Wait!.Reason : transition == "blocked" ? result.BlockReason : result.ResultSummary);
        Assert.Null(row.ClaimEventId);
    }
}
