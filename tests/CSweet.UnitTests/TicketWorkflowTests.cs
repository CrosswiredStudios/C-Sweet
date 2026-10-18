using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class TicketWorkflowTests
{
    [Fact]
    public void WorkflowTicketsDoNotReplaceTheDefaultExecutableTaskType() =>
        Assert.Equal(WorkItemTypeKeys.SoftwareTaskV2,PlatformWorkTypeCatalog.DefaultTypeKey(WorkBoardProfileKeys.SoftwareDeliveryV2,"Task"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task<WorkTask> Ticket(HierarchicalDeliveryServiceTests.Fixture f, params Guid[] predecessors)
    {
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id,
            Kind = WorkItemKind.Task, TypeKey = WorkItemTypeKeys.WorkflowTicketV1, IsExecutable = false, Title = "Agent-declared assessment",
            PlanningSpecificationJson = JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Deliver requirement"], ["Requirement works"])
                { DependencyItemIds = predecessors }, Json) };
        f.Db.CoreWorkTasks.Add(item); await f.Db.SaveChangesAsync(); return item;
    }
    private static WorkDeliveryTicketStep Step(string key, Guid employee) => new(key,"review",new(key,"Human",employee),"Assess the ticket criteria.");
    private static ConfigureWorkDeliveryPlanRequest Request(HierarchicalDeliveryServiceTests.Fixture f, params WorkDeliveryTicketWorkflow[] workflows)
    {
        foreach(var workflow in workflows) {
            var ticket=f.Db.CoreWorkTasks.Single(t=>t.Id==workflow.TicketId);
            var planning=JsonSerializer.Deserialize<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!,Json)!;
            ticket.PlanningSpecificationJson=JsonSerializer.Serialize(planning with {Workflow=workflow.Definition()},Json);
        }
        f.Db.SaveChanges();
        return new(f.Project,"Agent-defined work",f.Manager.Id,[],[],[],Guid.NewGuid().ToString("N")) { TicketWorkflows = workflows };
    }

    [Fact]
    public async Task TicketsDriveReadinessAndDeclaredOrderWithoutHierarchyOrInjectedGates()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var first = await Ticket(f,f.Task.Id); var last = await Ticket(f,first.Id);
        var request = Request(f,new(first.Id,[Step("inspect-contracts",f.Technical.Id),Step("confirm-outcome",f.Technical.Id)]),
            new(last.Id,[Step("publish-conclusion",f.Technical.Id)]));
        var draft = await f.Configure(request); await f.Activate(draft); await f.Service.PulseAsync();
        Assert.All((await f.Read()).Executions,e=>Assert.Equal("WaitingForChildren",e.Status));
        f.Task.Status = WorkTaskStatus.Completed; await f.Db.SaveChangesAsync(); await f.Service.PulseAsync();
        var active = (await f.Read()).Executions.Single(e=>e.ItemId==first.Id);
        Assert.Equal("inspect-contracts",active.CurrentStageKey); await f.Approve(active);
        active = (await f.Read()).Executions.Single(e=>e.ItemId==first.Id);
        Assert.Equal("confirm-outcome",active.CurrentStageKey); await f.Approve(active); await f.Service.PulseAsync();
        var final = (await f.Read()).Executions.Single(e=>e.ItemId==last.Id);
        Assert.Equal("publish-conclusion",final.CurrentStageKey); await f.Approve(final); await f.Service.PulseAsync();
        var completed = await f.Read(); Assert.Equal("Completed",completed.Status);
        Assert.Equal(new[]{"inspect-contracts","confirm-outcome"},completed.Executions.Single(e=>e.ItemId==first.Id).Stages.Select(s=>s.StageKey));
        Assert.All(completed.Executions,e=>Assert.NotNull(e.WorkflowTicketId));
        Assert.Equal(WorkTaskStatus.Completed,first.Status); Assert.Equal(WorkTaskStatus.Completed,last.Status);
        Assert.Contains(await f.Db.ApplicationRealtimeOutbox.ToListAsync(),e=>e.EventType==CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged);
    }

    [Fact]
    public async Task CyclicTicketDependenciesAreRejectedBeforeAnExecutionIsCreated()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var a = await Ticket(f); var b = await Ticket(f,a.Id);
        a.PlanningSpecificationJson = JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Deliver requirement"],["Requirement works"])
            { DependencyItemIds=[b.Id] },Json); await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(()=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,
            Request(f,new(a.Id,[Step("first",f.Technical.Id)]),new(b.Id,[Step("last",f.Technical.Id)]))));
        Assert.Contains("cycle",error.Message); Assert.Empty(await f.Db.WorkDeliveryExecutions.ToListAsync());
    }

    [Fact]
    public async Task ChangedTicketCriteriaCannotCompletePreviouslyDispatchedWork()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var ticket = await Ticket(f); await f.Activate(await f.Configure(Request(f,new WorkDeliveryTicketWorkflow(ticket.Id,[Step("review",f.Technical.Id)]))));
        await f.Service.PulseAsync(); var execution = (await f.Read()).Executions.Single();
        ticket.PlanningRevision++; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,f.Review(execution)));
        Assert.NotEqual(WorkTaskStatus.Completed,ticket.Status);
    }

    [Fact]
    public async Task DuplicateWakesAndServiceRestartDoNotCreateExtraWorkOrAcceptance()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var ticket = await Ticket(f);
        var request = Request(f, new WorkDeliveryTicketWorkflow(ticket.Id,[Step("assess",f.Technical.Id)]));
        var draft = await f.Configure(request);
        Assert.Equal(draft.Id,(await f.Service.ConfigureAsync(f.Org,f.Manager.Id,request)).Id);
        await f.Activate(draft);
        for (var i=0;i<4;i++) await f.Service.PulseAsync();
        var execution = Assert.Single((await f.Read()).Executions);
        Assert.Single(execution.Stages);
        Assert.Equal(WorkTaskStatus.WaitingForApproval,ticket.Status);
        var review = f.Review(execution);
        await f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,review);
        await f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,review);
        for (var i=0;i<4;i++) await f.Service.PulseAsync();
        Assert.Equal("Completed",(await f.Read()).Status);
        Assert.Single(await f.Db.WorkDeliveryExecutions.ToListAsync());
        Assert.Single(await f.Db.WorkStageExecutions.Where(s=>s.DeliveryExecutionId==execution.Id).ToListAsync());
    }

    [Fact]
    public async Task RejectedTicketBlocksOnlyItsDependentsWhileUnrelatedWorkContinues()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var rejected = await Ticket(f); var dependent = await Ticket(f,rejected.Id); var parallel = await Ticket(f);
        await f.Activate(await f.Configure(Request(f,
            new(rejected.Id,[Step("assess-a",f.Technical.Id)]),new(dependent.Id,[Step("after-a",f.Technical.Id)]),
            new(parallel.Id,[Step("assess-b",f.Technical.Id)]))));
        await f.Service.PulseAsync(); var active=await f.Read();
        var failure=active.Executions.Single(e=>e.WorkflowTicketId==rejected.Id);
        var result=f.Review(failure);
        await f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,result with {
            Result=result.Result with { Approved=false,Summary="Requirement fails",Criteria=[new("Requirement works",false,"Observed failure")],Findings=["Fix requirement"] } });
        await f.Approve(active.Executions.Single(e=>e.WorkflowTicketId==parallel.Id));
        await f.Service.PulseAsync();
        var current=await f.Read();
        Assert.Equal("Blocked",current.Executions.Single(e=>e.WorkflowTicketId==rejected.Id).Status);
        Assert.Empty(current.Executions.Single(e=>e.WorkflowTicketId==dependent.Id).Stages);
        Assert.Equal("Completed",current.Executions.Single(e=>e.WorkflowTicketId==parallel.Id).Status);
        Assert.NotEqual("Completed",current.Status);
    }

    [Fact]
    public async Task MissingWorkflowDeclarationCannotActivateTheFormerImplicitGateSequence()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,f.Request() with { TicketWorkflows=[] }));
        Assert.Empty(await f.Db.WorkDeliveryPlans.ToListAsync());
    }

    [Fact]
    public async Task AgentSdkCreatesRealBoardTicketsAndDeliveryFinishesWithoutSynthesizedReviews()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        var user=Guid.NewGuid();f.Manager.ApplicationUserId=user;
        f.Db.WorkBoardColumns.Add(new() {Id=Guid.NewGuid(),BoardId=f.Board.Id,Name="Ready",Category=WorkBoardColumnCategory.ToDo});
        f.Db.ScopedActionGrants.Add(new() {Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.OrganizationUser,
            SubjectId=f.Manager.Id,ScopeKind=GrantScopeKind.Board,ScopeId=f.Board.Id,Action=CSweet.Contracts.WorkManagement.WorkItemActions.Create});
        await f.Db.SaveChangesAsync();
        var boards=new WorkBoardService(f.Db,new ScopedActionAuthorizationService(f.Db),new Audit());
        var runtime=new AgentTestRuntime()
            .RegisterCapability<CreateWorkItemRequest,WorkItem>(WorkItemCapabilities.Create,async(r,ct)=> {
                var item=await boards.CreateItemAsync(f.Org,r.BoardId,user,new(r.Title,r.Description,r.Kind,r.Priority) {
                    TypeKey=r.TypeKey!,Planning=r.Planning,AccountableOrganizationUserId=r.AccountableOrganizationUserId },ct);
                return JsonSerializer.SerializeToElement(item,Json).Deserialize<WorkItem>(Json)!;
            })
            .RegisterCapability<ConfigureWorkDeliveryPlanRequest,WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Configure,
                (r,ct)=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,r,ct));
        WorkItem Item(WorkTask task)=>JsonSerializer.Deserialize<WorkItem>("{}")! with {
            Id=task.Id,Kind=task.Kind.ToString(),Title=task.Title,ParentItemId=task.ParentWorkTaskId,
            Planning=JsonSerializer.Deserialize<WorkItemPlanningSpecification>(task.PlanningSpecificationJson!,Json),PlanningRevision=task.PlanningRevision };
        var request=new ConfigureWorkDeliveryPlanRequest(f.Project,"Declared increment",f.Manager.Id,[f.Epic.Id],[],[
            new("Story",f.Story.Id,f.Board.Id,[new("source-preparation","Human",f.Technical.Id)]),
            new("Epic",f.Epic.Id,f.Board.Id,[new("collect-evidence","Human",f.Technical.Id)]),
            new("Release",null,f.Board.Id,[new("assess-outcome","Human",f.Technical.Id)])],"sdk-declare");
        var plan=await TicketWorkflowPlanning.ConfigureAsync(request,[Item(f.Epic),Item(f.Story),Item(f.Task)],runtime.CreateContext(),default);
        f.PlanId=plan.Id;
        f.Task.DeliverySpecificationJson=JsonSerializer.Serialize(new WorkItemDeliverySpecification(Guid.Empty,["Deliver requirement"],["Requirement works"]) {
            DeliveryPlanId=plan.Id,DeliveryKind="Artifact",BaseBranch="" },Json);
        await f.Db.SaveChangesAsync();
        var tickets=await f.Db.CoreWorkTasks.Where(t=>t.TypeKey==WorkItemTypeKeys.WorkflowTicketV1).ToListAsync();
        Assert.Equal(3,tickets.Count);
        Assert.All(tickets,t=>Assert.NotNull(JsonSerializer.Deserialize<WorkItemPlanningSpecification>(t.PlanningSpecificationJson!,Json)!.Workflow));
        Assert.Equal(3,await f.Db.WorkItemDependencies.CountAsync());
        await f.Activate(plan);await f.Service.PulseAsync();
        Assert.All((await f.Read()).Executions,e=>Assert.Empty(e.Stages));
        await f.CompleteTasks();
        for(var i=0;i<4;i++)await f.Service.PulseAsync();
        var review=(await f.Read()).Executions.Single(e=>e.Scope=="Release");
        Assert.Equal("assess-outcome",review.CurrentStageKey);
        await f.Approve(review);await f.Service.PulseAsync();
        Assert.Equal("Completed",(await f.Read()).Status);
        Assert.Equal(WorkTaskStatus.Completed,f.Epic.Status);
        Assert.Equal(WorkTaskStatus.Completed,f.Story.Status);
        Assert.DoesNotContain((await f.Read()).Executions.SelectMany(e=>e.Stages),s=>s.StageKey is "quality" or "technical-review" or "manager-review");
    }

    private sealed class Audit:IAuditEventWriter
    {
        public Task<Guid> AppendAsync(AuditEventWriteRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(Guid.NewGuid());
        public Task WriteAsync(string eventType,string entityType,Guid? entityId,string? summary,string? metadataJson=null,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    }
}
