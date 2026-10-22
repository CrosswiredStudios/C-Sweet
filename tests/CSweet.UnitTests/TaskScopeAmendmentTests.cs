using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Fixture = CSweet.UnitTests.HierarchicalDeliveryServiceTests.Fixture;

namespace CSweet.UnitTests;

public sealed class TaskScopeAmendmentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CriterionAmendmentPreservesLongAcceptedRequirementWithoutTruncation()
    {
        await using var fixture = await Fixture.Create(false);
        var (request, _) = await Prepare(fixture);
        var requirement = new string('r', 4308);
        request = request with { Planning = request.Planning with { Requirements = [requirement] } };
        await fixture.Service.AmendTaskAsync(fixture.Org, fixture.Manager.Id, request);
        Assert.Equal(requirement, JsonSerializer.Deserialize<WorkItemPlanningSpecification>(fixture.Task.PlanningSpecificationJson!, Json)!.Requirements.Single());
        Assert.Equal(requirement, JsonSerializer.Deserialize<WorkItemDeliverySpecification>(fixture.Task.DeliverySpecificationJson!, Json)!.Requirements.Single());
    }

    [Fact]
    public async Task AmendmentRepinsScopePreservesCandidateAndWaitsForExplicitRetry()
    {
        await using var f = await Fixture.Create(false);
        var (request, execution) = await Prepare(f);
        var publication = new CSweet.Domain.Setup.SourceControlPublication { Id=Guid.NewGuid(), OrganizationId=f.Org,
            RepositoryId=f.Repositories[0], WorkspaceId=Guid.NewGuid(), CommitSha=new string('a',40),
            TicketBranch="csweet/task/preserved",TargetBranch="csweet/story/one" };
        f.Db.SourceControlPublications.Add(publication); await f.Db.SaveChangesAsync();
        var before = f.Task.AssignmentRevision;
        var amended = await f.Service.AmendTaskAsync(f.Org,f.Manager.Id,request);
        Assert.Equal("Paused",amended.Status); Assert.Equal(request.ExpectedPlanRevision+1,amended.Revision);
        Assert.Equal(2,amended.ScopeRevision); Assert.Equal(2,f.Task.PlanningRevision);
        Assert.Equal(before+1,f.Task.AssignmentRevision); Assert.Equal(WorkTaskStatus.Blocked,f.Task.Status);
        Assert.Equal(request.Planning.AcceptanceCriteria,JsonSerializer.Deserialize<WorkItemDeliverySpecification>(f.Task.DeliverySpecificationJson!,Json)!.AcceptanceCriteria);
        Assert.Equal("development",execution.CurrentStageKey); Assert.Equal(1,execution.Traversal);
        Assert.Equal(WorkStageExecutionStatus.Cancelled,execution.Stages.Single(s=>s.Traversal==0).Status);
        Assert.Equal(WorkStageExecutionStatus.Blocked,execution.Stages.Single(s=>s.Traversal==1).Status);
        Assert.Empty(execution.Stages.Single(s=>s.Traversal==1).Attempts);
        Assert.Equal(new string('a',40),(await f.Db.SourceControlPublications.SingleAsync()).CommitSha);
        Assert.All(await f.Db.WorkDeliveryExecutions.Where(e=>e.PlanId==amended.Id && e.ScopeRevision==1).ToListAsync(),e=>Assert.Equal("Superseded",e.Status));
        Assert.Equal(3,await f.Db.WorkDeliveryExecutions.CountAsync(e=>e.PlanId==amended.Id && e.ScopeRevision==2));
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(),e=>e.EventType==WorkDeliveryCapabilities.Changed);
        Assert.Equal(amended.Revision,(await f.Service.AmendTaskAsync(f.Org,f.Manager.Id,request)).Revision);
        // Resume validates the newly pinned task digest and does not start its blocked writer.
        await f.Service.ControlAsync(f.Org,f.Manager.Id,new(amended.Id,amended.Revision,"resume","resume-amended"));
        Assert.Equal(WorkStageExecutionStatus.Blocked,execution.Stages.Single(s=>s.Traversal==1).Status);
    }

    [Theory]
    [InlineData("actor")][InlineData("plan")][InlineData("item")][InlineData("planning")]
    [InlineData("dependencies")][InlineData("active-attempt")][InlineData("preview-stage")][InlineData("architecture")][InlineData("empty-criterion")][InlineData("long-entry")]
    public async Task ChangedAuthorityOrActiveWorkCannotAmendScope(string fault)
    {
        await using var f=await Fixture.Create(false);
        var (request,execution)=await Prepare(f);
        request=fault switch
        {
            "plan"=>request with {ExpectedPlanRevision=request.ExpectedPlanRevision-1},
            "item"=>request with {ExpectedItemRevision=request.ExpectedItemRevision+1},
            "planning"=>request with {ExpectedPlanningRevision=request.ExpectedPlanningRevision+1},
            "dependencies"=>request with {Planning=request.Planning with {DependencyItemIds=[Guid.NewGuid()]}},
            "architecture"=>request with {Planning=request.Planning with {ArchitectureArtifactDigest=new string('a',64)}},
            "empty-criterion"=>request with {Planning=request.Planning with {AcceptanceCriteria=[""]}},
            "long-entry"=>request with {Planning=request.Planning with {Requirements=[new string('r',8193)]}},
            "preview-stage"=>request with {UsePlatformPreview=true},
            _=>request
        };
        if(fault=="active-attempt")
        {
            var attempt=new WorkExecutionAttempt {Id=Guid.NewGuid(),Status=WorkExecutionAttemptStatus.Running,
                StageExecutionId=execution.Stages.Single().Id};
            f.Db.WorkExecutionAttempts.Add(attempt);
            await f.Db.SaveChangesAsync();
        }
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.AmendTaskAsync(f.Org,fault=="actor"?f.Technical.Id:f.Manager.Id,request));
        Assert.Equal(1,f.Task.PlanningRevision); Assert.Equal(0,execution.Traversal);
        Assert.Equal(1,(await f.Read()).ScopeRevision);
    }

    [Fact]
    public async Task PlatformPreviewKeepsInternalGitAndRequiresAssignedReleaseReadiness()
    {
        await using var f=await Fixture.Create(false);
        var (request,_)=await Prepare(f,preview:true);
        var amended=await f.Service.AmendTaskAsync(f.Org,f.Manager.Id,request with {UsePlatformPreview=true});
        var release=Assert.Single(amended.Branches,b=>b.Scope=="Release");
        Assert.Equal("compute-test",release.ReadinessMode); Assert.Null(release.Build);
        Assert.Contains(amended.Scopes.Single(s=>s.Scope=="Release").Stages,s=>s.StageKey=="build-readiness");
    }

    private static async Task<(AmendWorkDeliveryTaskRequest,WorkItemExecution)> Prepare(Fixture f,bool preview=false)
    {
        var configuration=f.Request();
        if(preview) configuration=configuration with {
            Branches=configuration.Branches.Select(b=>b.Scope=="Release"?b with {ReadinessMode="compute-test"}:b).ToArray(),
            TicketWorkflows=configuration.TicketWorkflows.Select(w=>w.Scope=="Release"?w with {Steps=[new("build-readiness","build-readiness",new("build-readiness","Human",f.Technical.Id)),..w.Steps]}:w).ToArray()};
        var active=await f.Activate(await f.Configure(configuration));
        var paused=await f.Service.ControlAsync(f.Org,f.Manager.Id,new(active.Id,active.Revision,"pause","amend-pause"));
        var policyId=Guid.NewGuid();
        f.Db.WorkOrchestrationPolicyRevisions.Add(new(){Id=policyId,OrganizationId=f.Org,BoardId=f.Board.Id,
            Stages=[new(){Id=Guid.NewGuid(),PolicyRevisionId=policyId,Key="development",Type=WorkOrchestrationStageType.MemberExecution}]});
        var sprint=new WorkSprintExecution {Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,
            PolicyRevisionId=policyId,Status=WorkSprintExecutionStatus.Active};
        var execution=new WorkItemExecution {Id=Guid.NewGuid(),WorkItemId=f.Task.Id,WorkItem=f.Task,
            SprintExecutionId=sprint.Id,SprintExecution=sprint,CurrentStageKey="technical-review",Status=WorkItemExecutionStatus.Blocked};
        execution.Stages.Add(new(){Id=Guid.NewGuid(),StageKey="technical-review",Status=WorkStageExecutionStatus.Blocked});
        f.Db.AddRange(sprint,execution); f.Task.Status=WorkTaskStatus.Blocked; await f.Db.SaveChangesAsync();
        var planning=JsonSerializer.Deserialize<WorkItemPlanningSpecification>(f.Task.PlanningSpecificationJson!,Json)! with
            {Requirements=["Use Ready InternalGit and the platform build/preview path"],AcceptanceCriteria=["Build pinned source and verify the exact preview"]};
        return (new(paused.Id,f.Task.Id,paused.Revision,f.Task.Revision,f.Task.PlanningRevision,planning,
            "Owner approved the MVP platform path instead of GitHub-only deployment.","amend-approved",false),execution);
    }
}
