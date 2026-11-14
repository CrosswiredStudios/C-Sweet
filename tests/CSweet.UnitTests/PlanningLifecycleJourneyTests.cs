using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Security;

namespace CSweet.UnitTests;

public sealed class PlanningLifecycleJourneyTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshArtifactPlanAndRetainedOwnerAmendmentUseTheSameAuthorizedReconciliationAcrossReplay(bool legacySnapshot)
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());
        var retained=await f.Read();
        var originalInputs=retained.Scopes.SelectMany(s=>s.ChildInputs??[]).DistinctBy(i=>i.ItemId).ToArray();
        var originalAssignments=retained.Scopes.SelectMany(s=>s.ChildAssignments??[]).DistinctBy(a=>a.ItemId).ToArray();
        if(legacySnapshot) {
            var stored=await f.Db.WorkDeliveryPlans.SingleAsync(p=>p.Id==retained.Id);
            stored.ScopesJson=JsonSerializer.Serialize(retained.Scopes.Select(s=>s with {ChildAssignments=null,ChildInputs=null}),Json);
            await f.Db.SaveChangesAsync();
        }
        _=await WorkDeliveryTaskAuthorization.RequireAsync(f.Db,f.Task,default);
        var planning=JsonSerializer.Deserialize<WorkItemPlanningSpecification>(f.Task.PlanningSpecificationJson!,Json)!;
        var delivery=JsonSerializer.Deserialize<WorkItemDeliverySpecification>(f.Task.DeliverySpecificationJson!,Json)!;
        f.Task.PlanningRevision++;
        f.Task.PlanningSpecificationJson=JsonSerializer.Serialize(planning with {Constraints=["Versioned approved document is the delivery evidence"]},Json);
        f.Task.DeliverySpecificationJson=JsonSerializer.Serialize(delivery with {Constraints=["Versioned approved document is the delivery evidence"]},Json);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>WorkDeliveryTaskAuthorization.RequireAsync(f.Db,f.Task,default));
        var before=await f.Read();
        var runtime=new AgentTestRuntime().RegisterCapability<ControlWorkDeliveryPlanRequest,WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Control,
            (r,ct)=>f.Service.ControlAsync(f.Org,f.Manager.Id,r,ct));
        var accepted=new Dictionary<Guid,long> {[f.Task.Id]=f.Task.PlanningRevision};
        var recovered=await DeliveryInputReconciliation.ApplyAsync(before,accepted,runtime.CreateContext(),default,originalAssignments,originalInputs);
        var replay=await DeliveryInputReconciliation.ApplyAsync(before,accepted,runtime.CreateContext(),default,originalAssignments,originalInputs);
        Assert.Equal(recovered.ScopeRevision,replay.ScopeRevision);
        Assert.Equal(before.ScopeRevision+1,recovered.ScopeRevision);
        var freshIds=await f.Db.WorkDeliveryExecutions.Where(e=>e.PlanId==before.Id&&e.ScopeRevision==recovered.ScopeRevision).Select(e=>e.Id).ToListAsync();
        Assert.All(recovered.Executions.Where(e=>freshIds.Contains(e.Id)),e=>Assert.NotEqual("Completed",e.Status));
        Assert.Contains(recovered.Executions,e=>e.Status=="Superseded");
        _=await WorkDeliveryTaskAuthorization.RequireAsync(f.Db,f.Task,default);
        Assert.Equal(planning.AcceptanceCriteria,JsonSerializer.Deserialize<WorkItemPlanningSpecification>(f.Task.PlanningSpecificationJson!,Json)!.AcceptanceCriteria);
        Assert.Single(await f.Db.WorkDeliveryMutationReceipts.Where(r=>r.Operation=="control"&&r.IdempotencyKey.StartsWith("delivery-input-reconciliation")).ToListAsync());
    }

    [Fact]
    public async Task AnApprovedTaskAmendmentCannotAdoptUnrelatedWorkflowChangesOrResumeAnOwnerPause()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await f.Activate(await f.Configure());var before=await f.Read();
        var workflow=await f.Db.CoreWorkTasks.SingleAsync(t=>t.Id==before.Scopes[0].TicketWorkflow!.TicketId);
        workflow.PlanningRevision++;await f.Db.SaveChangesAsync();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ControlAsync(f.Org,f.Manager.Id,
            new(before.Id,before.Revision,"reconcile-inputs","reject-unapproved") {PlanningChanges=[]}));
        Assert.Contains("Unapproved",error.Message);
        Assert.Equal(before.ScopeRevision,(await f.Read()).ScopeRevision);
        var paused=await f.Service.ControlAsync(f.Org,f.Manager.Id,new(before.Id,before.Revision,"pause","owner-pause"));
        var unchanged=await DeliveryInputReconciliation.ApplyAsync(paused,new Dictionary<Guid,long> {[workflow.Id]=workflow.PlanningRevision},
            new AgentTestRuntime().CreateContext(),default);
        Assert.Equal("Paused",unchanged.Status);Assert.Equal(paused.Revision,unchanged.Revision);
    }

    [Fact]
    public async Task SprintReworkAndAcceptanceLeadToOneExplicitFinalProductAndEvidenceBoundClosure()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        await HierarchicalTaskRuntimeTests.RunJourney(f,true);
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(),e=>e.EventType==WorkstreamEventNames.SprintChangedV1);
        for(var i=0;i<12;i++) {
            await f.Service.PulseAsync();var current=await f.Read();if(current.Status=="Completed")break;
            var review=current.Executions.FirstOrDefault(e=>e.Status=="WaitingForHuman");
            if(review is not null){await f.Approve(review);continue;}
            var acceptance=current.Executions.FirstOrDefault(e=>e.Status=="WaitingForApproval");
            if(acceptance is not null)await f.Accept(acceptance);
        }
        var increment=await f.Read();Assert.Equal("Completed",increment.Status);
        var sourceId=Guid.NewGuid();var revisionId=Guid.NewGuid();var digest=new string('a',64);
        f.Db.CoreArtifacts.Add(new(){Id=sourceId,OrganizationId=f.Org,WorkstreamId=f.Project,Title="Approved outcome",AcceptedRevisionId=revisionId,LatestRevisionId=revisionId});
        f.Db.ArtifactRevisions.Add(new(){Id=revisionId,ArtifactId=sourceId,OrganizationId=f.Org,Content="Requirement works",ContentSha256=digest,Status=ArtifactRevisionStatus.Accepted});
        var user=Guid.NewGuid();f.Manager.ApplicationUserId=user;
        f.Db.Users.Add(new CSweet.Infrastructure.Auth.ApplicationUser {Id=user,UserName=user.ToString()});
        foreach(var action in new[]{CSweet.Contracts.WorkManagement.WorkItemActions.Create,CSweet.Contracts.WorkManagement.WorkBoardActions.Read})
            f.Db.ScopedActionGrants.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=CSweet.Domain.Security.GrantSubjectKind.OrganizationUser,
                SubjectId=f.Manager.Id,ScopeKind=CSweet.Domain.Security.GrantScopeKind.Board,ScopeId=f.Board.Id,Action=action});
        if(!await f.Db.WorkBoardColumns.AnyAsync(c=>c.BoardId==f.Board.Id))
            f.Db.WorkBoardColumns.Add(new(){Id=Guid.NewGuid(),BoardId=f.Board.Id,Name="Ready",Category=CSweet.Domain.WorkManagement.WorkBoardColumnCategory.ToDo});
        await f.Db.SaveChangesAsync();
        var boards=new WorkBoardService(f.Db,new ScopedActionAuthorizationService(f.Db),new Audit());
        var states=new Dictionary<string,AgentOperatingStateResponse>();
        var runtime=new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=> {
                var now=DateTimeOffset.UtcNow;var value=new AgentOperatingStateResponse(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,1,now,now);
                states[r.StateKey]=value;return Task.FromResult(value);
            })
            .RegisterCapability<WorkBoardReference,WorkBoardDetail>(WorkItemCapabilities.Read,async(r,ct)=>
                JsonSerializer.SerializeToElement(await boards.GetAsync(f.Org,r.BoardId,user,ct),Json).Deserialize<WorkBoardDetail>(Json)!)
            .RegisterCapability<CreateWorkItemRequest,WorkItem>(WorkItemCapabilities.Create,async(r,ct)=> {
                var item=await boards.CreateItemAsync(f.Org,r.BoardId,user,new(r.Title,r.Description,r.Kind,r.Priority) {
                    TypeKey=r.TypeKey,Planning=r.Planning,AccountableOrganizationUserId=r.AccountableOrganizationUserId },ct);
                return JsonSerializer.SerializeToElement(item,Json).Deserialize<WorkItem>(Json)!;
            })
            .RegisterCapability<ConfigureWorkDeliveryPlanRequest,WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Configure,(r,ct)=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,r,ct))
            .RegisterCapability<ControlWorkDeliveryPlanRequest,WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Control,(r,ct)=>f.Service.ControlAsync(f.Org,f.Manager.Id,r,ct))
            .RegisterCapability<ReadWorkDeliveryPlansRequest,IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read,(r,ct)=>f.Service.ReadAsync(f.Org,f.Manager.Id,r,ct));
        var approved=new WorkApprovedSource(sourceId,revisionId,digest);
        var specification=new FinalProductSpecification(f.Project,f.Board.Id,f.Manager.Id,digest,new(["Deliver requirement"],["Requirement works"]),
            [increment.Scopes.Single(s=>s.Scope=="Release").TicketWorkflow!.TicketId],[],
            [new("assess-product","review",new("assess-product","Human",f.Technical.Id))]) {CompletionSource=approved};
        var final=await FinalProductDelivery.PrepareAsync(specification,runtime.CreateContext(),default);
        var proof=new WorkstreamDeliveryCompletion(final.Id,final.ScopeRevision,approved);
        var project=await f.Db.Workstreams.SingleAsync(p=>p.Id==f.Project);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkstreamDeliveryCompletionValidator.ValidateAsync(f.Db,project,proof,default));
        var completionService=new WorkstreamDeliveryCompletionService(f.Db,TimeProvider.System);
        var complete=new CompleteWorkstreamDeliveryRequest(f.Project,project.Revision,proof,"verified-project-close");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>completionService.CompleteAsync(f.Org,f.Author.Id,complete,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>completionService.CompleteAsync(f.Org,f.Manager.Id,complete,default));
        f.PlanId=final.Id;await f.Service.PulseAsync();await f.Approve((await f.Read()).Executions.Single());await f.Service.PulseAsync();
        Assert.Equal("Completed",(await f.Read()).Status);
        await WorkstreamDeliveryCompletionValidator.ValidateAsync(f.Db,project,proof,default);
        var replay=await FinalProductDelivery.PrepareAsync(specification,runtime.CreateContext(),default);
        Assert.Equal(final.Id,replay.Id);Assert.Equal(2,await f.Db.WorkDeliveryPlans.CountAsync());
        Assert.Single(await f.Db.WorkSprints.ToListAsync()); // Final asynchronous acceptance creates no empty successor sprint.
        var lifecycleStage=project.LifecycleStage;
        var participation=await f.Db.ProjectParticipants.SingleAsync(p=>p.WorkstreamId==f.Project&&p.OrganizationUserId==f.Manager.Id);
        participation.RemovedAt=DateTimeOffset.UtcNow;await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>completionService.CompleteAsync(f.Org,f.Manager.Id,complete,default));
        participation.RemovedAt=null;await f.Db.SaveChangesAsync();
        var closed=await completionService.CompleteAsync(f.Org,f.Manager.Id,complete,default);
        Assert.True(closed.Applied);Assert.Equal(WorkstreamStatus.Completed,project.Status);Assert.Equal(lifecycleStage,project.LifecycleStage);
        Assert.Equal(closed.Revision,(await completionService.CompleteAsync(f.Org,f.Manager.Id,complete,default)).Revision);
        Assert.Single(await f.Db.WorkDeliveryMutationReceipts.Where(r=>r.Operation=="project-completion").ToListAsync());
        (await f.Db.CoreArtifacts.SingleAsync(a=>a.Id==sourceId)).AcceptedRevisionId=Guid.NewGuid();await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkstreamDeliveryCompletionValidator.ValidateAsync(f.Db,project,proof,default));
    }

    private sealed class Audit:IAuditEventWriter
    {
        public Task<Guid> AppendAsync(AuditEventWriteRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(Guid.NewGuid());
        public Task WriteAsync(string eventType,string entityType,Guid? entityId,string? summary,string? metadataJson=null,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    }
}
