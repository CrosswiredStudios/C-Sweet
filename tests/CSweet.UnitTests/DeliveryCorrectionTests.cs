using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class DeliveryCorrectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<WorkDeliveryPlanResponse> Reject(HierarchicalDeliveryServiceTests.Fixture f, bool tasksCompleted = false)
    {
        if (!tasksCompleted) { await f.Activate(await f.Configure()); await f.CompleteTasks(); }
        for (var i=0;i<8;i++)
        {
            await f.Service.PulseAsync(); var plan=await f.Read();
            var release=plan.Executions.Single(e=>e.Scope=="Release");
            if (release.Stages.Count>0)
            {
                var request=f.Review(release);
                return await f.Service.CompleteReviewAsync(f.Org,f.Qa.Id,request with { Result=request.Result with {
                    Approved=false,Summary="The explosive sample selects the wrong brick",Criteria=[new("Requirement works",false,"Observed wrong sample")],
                    Findings=["Correct the explosive sample coordinate and retain a regression assertion."] } });
            }
            var current=plan.Executions.First(e=>e.Status is "WaitingForHuman" or "WaitingForApproval");
            if (current.Status=="WaitingForHuman") await f.Approve(current); else await f.Accept(current);
        }
        throw new InvalidOperationException("Release did not start.");
    }

    [Fact]
    public async Task Accepted_rejection_atomically_retains_one_owned_handoff_without_a_report_or_active_sprint()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var rejected=await Reject(f);
        var correction=Assert.Single(rejected.Corrections);
        Assert.Equal("Pending",correction.Status); Assert.Equal(f.Manager.Id,correction.OwnerOrganizationUserId);
        Assert.Equal(Assert.Single(rejected.Findings).Id,Assert.Single(correction.FindingIds));
        Assert.Equal(WorkTaskStatus.Completed,f.Task.Status); Assert.Empty(await f.Db.WorkSprints.ToListAsync());
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(),e=>e.EventType==WorkDeliveryCapabilities.Changed&&e.DataJson.Contains("correction.requested"));
        for(var i=0;i<3;i++) await f.Service.PulseAsync(); // Each Service property constructs a fresh service.
        var current=await f.Read(); Assert.Equal(JsonSerializer.Serialize(correction,Json),JsonSerializer.Serialize(Assert.Single(current.Corrections),Json));
        Assert.Single(current.Findings); Assert.Equal("Open",current.Findings[0].Status);
    }

    [Fact]
    public async Task Retained_pre_upgrade_rejection_is_discovered_once_after_restart()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false); await Reject(f);
        f.Db.WorkDeliveryPlans.Single().CorrectionsJson="[]"; await f.Db.SaveChangesAsync();
        await f.Service.PulseAsync();var first=Assert.Single((await f.Read()).Corrections);
        await f.Service.PulseAsync();Assert.Equal(first.Id,Assert.Single((await f.Read()).Corrections).Id);
    }

    [Fact]
    public async Task Source_selection_is_durable_and_replayable_before_any_corrective_work_exists()
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var rejected = await Reject(f);
        var correction = Assert.Single(rejected.Corrections);
        var taskCount = await f.Db.CoreWorkTasks.CountAsync();
        var request = new CoordinateWorkDeliveryCorrectionRequest(rejected.Id, correction.Id, correction.Revision,
            "source-selection", SourceItemId: f.Task.Id,
            WaitingReason: "The retained scene finding belongs to this accepted implementation scope.");

        var selected = await f.Service.CoordinateCorrectionAsync(f.Org, f.Manager.Id, request);
        var saved = Assert.Single(selected.Corrections);
        Assert.Equal(f.Task.Id, saved.SourceItemId);
        Assert.Equal("NeedsPlanning", saved.Status);
        Assert.Null(saved.FollowUpPlanId);
        Assert.Empty(saved.RemediationTaskIds!);
        Assert.Equal("Active", selected.Status);
        Assert.Equal("Open", Assert.Single(selected.Findings).Status);
        Assert.Equal(taskCount, await f.Db.CoreWorkTasks.CountAsync());
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);

        f.Db.ChangeTracker.Clear();
        var replay = await f.Service.CoordinateCorrectionAsync(f.Org, f.Manager.Id, request);
        Assert.Equal(selected.Revision, replay.Revision);
        Assert.Equal(JsonSerializer.Serialize(saved, Json), JsonSerializer.Serialize(Assert.Single(replay.Corrections), Json));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Service.CoordinateCorrectionAsync(f.Org, f.Manager.Id,
            request with { IdempotencyKey = "racing-source-selection" }));
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(),
            entry => entry.EventType == WorkDeliveryCapabilities.Changed && entry.DataJson.Contains("correction.changed"));
    }

    [Fact]
    public async Task Missing_engineer_retains_a_specific_manager_action_and_duplicate_requests_do_not_reassign()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);var plan=await Reject(f);
        var correction=Assert.Single(plan.Corrections);
        var request=new CoordinateWorkDeliveryCorrectionRequest(plan.Id,correction.Id,correction.Revision,"wait-for-engineer",
            WaitingReason:"Restore the assigned engineer's current execution grant; no substitute has been authorized.");
        var waiting=await f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,request);
        var result=Assert.Single(waiting.Corrections);Assert.Equal("NeedsPlanning",result.Status);
        Assert.Equal(f.Manager.Id,result.OwnerOrganizationUserId);Assert.Null(result.FollowUpPlanId);
        Assert.Equal(waiting.Revision,(await f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,request)).Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,
            request with { IdempotencyKey="stale",WaitingReason="Changed" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.CoordinateCorrectionAsync(f.Org,f.Technical.Id,
            request with { ExpectedRevision=result.Revision,IdempotencyKey="wrong-manager" }));
        var grant=await f.Db.ScopedActionGrants.SingleAsync(g=>g.SubjectId==f.Manager.Id&&g.Action==WorkDeliveryCapabilities.Correct);
        grant.RevokedAt=DateTimeOffset.UtcNow;await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,
            request with { ExpectedRevision=result.Revision,IdempotencyKey="revoked" }));
        Assert.Equal("Open",Assert.Single((await f.Read()).Findings).Status);
    }

    [Fact]
    public async Task A_report_or_foreign_follow_up_cannot_claim_the_finding_was_fixed()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);var plan=await Reject(f);
        var c=Assert.Single(plan.Corrections);
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,
            new(plan.Id,c.Id,c.Revision,"no-evidence")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,
            new(plan.Id,c.Id,c.Revision,"foreign-source",SourceItemId:Guid.NewGuid(),WaitingReason:"fixed")));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,
            new(plan.Id,c.Id,c.Revision,"same-plan",SourceItemId:f.Task.Id,FollowUpPlanId:plan.Id,RemediationTaskIds:[f.Task.Id])));
        Assert.Equal("Open",Assert.Single((await f.Read()).Findings).Status);
    }

    [Fact]
    public async Task A_separate_correction_plan_must_finish_independent_qa_and_fresh_acceptance_before_superseding_the_rejection()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);var original=await Reject(f);
        var correction=Assert.Single(original.Corrections);var source=f.Task;
        var fix=new WorkTask {Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,ParentWorkTaskId=source.ParentWorkTaskId,
            Kind=source.Kind,TypeKey=source.TypeKey,Title="Correct the sample",IsExecutable=true,PlanningRevision=1,CreatedAt=DateTimeOffset.UtcNow,
            PlanningSpecificationJson=source.PlanningSpecificationJson};
        f.Db.CoreWorkTasks.Add(fix);
        var assignments=await f.Db.WorkItemStageAssignments.Where(a=>a.WorkItemId==source.Id).ToArrayAsync();
        var selected=assignments.Select(a=>new WorkStageAssignment(a.StageKey,a.PrincipalKind.ToString(),a.OrganizationUserId,a.AgentInstallationId,a.PlatformAction)).ToArray();
        foreach(var a in assignments)f.Db.WorkItemStageAssignments.Add(new() {Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,WorkItemId=fix.Id,
            StageKey=a.StageKey,PrincipalKind=a.PrincipalKind,OrganizationUserId=a.OrganizationUserId,AgentInstallationId=a.AgentInstallationId,PlatformAction=a.PlatformAction});
        var prior=original.Scopes.Single(s=>s.Scope=="Release");
        var prepId=Guid.NewGuid();var finalId=Guid.NewGuid();
        var steps=prior.TicketWorkflow!.Steps.OrderBy(s=>s.Key=="technical-review"?0:s.Key=="quality"?1:2).ToArray();
        var prep=new WorkDeliveryTicketWorkflow(prepId,[new("source-preparation","completion",new("source-preparation","Human",f.Manager.Id))]) {
            Scope="Story",ScopeItemId=f.Story.Id,EvidenceItemIds=[fix.Id],PromoteOnCompletion=true};
        var final=prior.TicketWorkflow with {TicketId=finalId,Steps=steps,EvidenceItemIds=prior.ChildIds.Append(prepId).ToArray()};
        void Ticket(WorkDeliveryTicketWorkflow workflow,Guid[] dependencies)
        {
            f.Db.CoreWorkTasks.Add(new() {Id=workflow.TicketId,OrganizationId=f.Org,BoardId=f.Board.Id,Kind=WorkItemKind.Task,Title="Corrective workflow",
                TypeKey=WorkItemTypeKeys.WorkflowTicketV1,IsExecutable=false,PlanningRevision=1,PlanningSpecificationJson=JsonSerializer.Serialize(prior.Planning with {
                    DependencyItemIds=dependencies,Workflow=workflow.Definition()},Json)});
        }
        Ticket(prep,[fix.Id]);Ticket(final,prior.PredecessorItemIds.Append(prepId).ToArray());await f.Db.SaveChangesAsync();
        var next=await f.Service.ConfigureAsync(f.Org,f.Manager.Id,new(f.Project,"Corrected increment",f.Manager.Id,original.EpicItemIds,
            original.Branches.Where(b=>b.Scope is "Story" or "Release").ToArray(),[],"follow-up") {
            TicketWorkflows=[prep,final],TaskAssignments=[new(fix.Id,selected)]});
        var delivery=JsonSerializer.Deserialize<WorkItemDeliverySpecification>(source.DeliverySpecificationJson!,Json)!;
        fix.DeliverySpecificationJson=JsonSerializer.Serialize(delivery with {DeliveryPlanId=next.Id},Json);await f.Db.SaveChangesAsync();
        var bound=await f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,new(original.Id,correction.Id,correction.Revision,"bind",source.Id,next.Id,[fix.Id]));
        Assert.Equal("Paused",bound.Status);Assert.Equal("Open",Assert.Single(bound.Findings).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ControlAsync(f.Org,f.Manager.Id,
            new(bound.Id,bound.Revision,"resume","cannot-restart-old-writer")));
        Assert.Equal("changes_requested",bound.Executions.Single(e=>e.Id==correction.ExecutionId).Stages.Last().LastOutcomeCode);
        next=await f.Service.ControlAsync(f.Org,f.Manager.Id,new(next.Id,next.Revision,"activate","activate-correction"));
        fix.Status=WorkTaskStatus.Completed;
        var completed=new WorkItemExecution {Id=Guid.NewGuid(),WorkItemId=fix.Id,Status=WorkItemExecutionStatus.Completed};
        completed.Stages.Add(new() {Id=Guid.NewGuid(),StageKey="quality",Status=WorkStageExecutionStatus.Completed,LastOutcomeCode="passed",CompletedAt=DateTimeOffset.UtcNow});
        f.Db.WorkItemExecutions.Add(completed);await f.Db.SaveChangesAsync();
        foreach(var branch in next.Branches.Where(b=>b.Scope=="Story"))f.Host.AdvanceStory(branch.RepositoryId,branch.SourceBranch);
        f.PlanId=next.Id;
        for(var i=0;i<8;i++)
        {
            await f.Service.PulseAsync();next=await f.Read();if(next.Status=="Completed")break;
            var review=next.Executions.FirstOrDefault(e=>e.Status is "WaitingForHuman" or "WaitingForApproval");
            if(review?.Status=="WaitingForHuman")await f.Approve(review);else if(review is not null)await f.Accept(review);
        }
        Assert.Equal("Completed",next.Status);
        var c=Assert.Single(bound.Corrections);
        var finished=await f.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,new(original.Id,c.Id,c.Revision,"finish"));
        Assert.Equal("Completed",Assert.Single(finished.Corrections).Status);Assert.Equal("Resolved",Assert.Single(finished.Findings).Status);
        Assert.Equal("Superseded",finished.Executions.Single(e=>e.Id==correction.ExecutionId).Status);
        Assert.Equal(WorkTaskStatus.Completed,source.Status);
        Assert.Equal("changes_requested",finished.Executions.Single(e=>e.Id==correction.ExecutionId).Stages.Last().LastOutcomeCode);
    }
}
