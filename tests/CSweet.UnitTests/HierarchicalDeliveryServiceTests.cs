using System.Reflection;
using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class HierarchicalDeliveryServiceTests
{
    [Theory]
    [InlineData(true, false, true)] [InlineData(false, false, false)] [InlineData(true, true, false)]
    public async Task OwnerCanRestartFailedAggregateProcessorButCannotResolveProductRejection(bool processorFailure, bool openFinding, bool allowed)
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single(e => e.Scope == "Story")); await f.Service.PulseAsync();
        var entity = await f.Db.WorkDeliveryExecutions.SingleAsync(e => e.Scope == "Epic");
        entity.Status = "Blocked"; entity.BlockedReason = processorFailure ? "agent-failure:v1;code=agent.invalid_operation;diagnosticId=retained" : "Actual product defect";
        f.Author.PermissionLevel = OrganizationPermissionLevel.Owner;
        if (openFinding) f.Db.Add(new WorkDeliveryFinding { Id = Guid.NewGuid(), PlanId = f.PlanId, ExecutionId = entity.Id,
            CandidateDigest = "retained", FindingDigest = "defect", Summary = "Actual unresolved defect", Status = "Open" });
        await f.Db.SaveChangesAsync();
        var request = new RecoverWorkDeliveryRequest(f.PlanId, entity.Id, entity.Revision, "owner-review-processor-recovery", "Install tested bounded JSON correction; rerun independent review without accepting the invalid response.");
        if (!allowed) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RecoverAsync(f.Org, f.Author.Id, request)); return; }
        var recovered = await f.Service.RecoverAsync(f.Org, f.Author.Id, request);
        Assert.Equal(recovered.Revision, (await f.Service.RecoverAsync(f.Org, f.Author.Id, request)).Revision);
        Assert.DoesNotContain(recovered.Executions, e => e.Scope == "Epic" && e.Status == "Completed");
        Assert.Null(entity.AcceptanceJson);
        Assert.Equal(f.Author.Id, (await f.Db.WorkDeliveryMutationReceipts.SingleAsync(r => r.IdempotencyKey == request.IdempotencyKey)).ActorId);
    }

    [Fact]
    public async Task OwnerCanRecoverExpiredReviewLeaseWithoutAcceptingTheCandidate()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single(e => e.Scope == "Story")); await f.Service.PulseAsync();
        var execution = await f.Db.WorkDeliveryExecutions.SingleAsync(e => e.Scope == "Epic");
        execution.Status = "Blocked"; execution.BlockedReason = "The prior runtime lease expired.";
        execution.Stages.Single(s => s.StageKey == "technical-review").Status = WorkStageExecutionStatus.Blocked;
        f.Author.PermissionLevel = OrganizationPermissionLevel.Owner; await f.Db.SaveChangesAsync();
        var candidate = execution.CandidateJson;
        await f.Service.RecoverAsync(f.Org, f.Author.Id, new(f.PlanId, execution.Id, execution.Revision, "expired-review-runtime-ready", "Replacement runtime is ready; restart independent review after lease expiry."));
        Assert.Equal("WaitingForChildren", execution.Status); Assert.Null(execution.AcceptanceJson);
        Assert.Equal(candidate, execution.CandidateJson);
    }







    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ScopedOwnerCanPauseAndResumeManagerPlanButOtherContributorsCannot(bool owner)
    {
        await using var f=await Fixture.Create(false);
        var plan=await f.Activate(await f.Configure(f.Request()));
        f.Author.PermissionLevel=owner?OrganizationPermissionLevel.Owner:OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        if(!owner) {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ControlAsync(f.Org,f.Author.Id,
                new(plan.Id,plan.Revision,"pause","owner-amendment-pause")));
            Assert.Equal("Active",(await f.Read()).Status);return;
        }
        plan=await f.Service.ControlAsync(f.Org,f.Author.Id,new(plan.Id,plan.Revision,"pause","owner-amendment-pause"));
        Assert.Equal("Paused",plan.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ControlAsync(f.Org,f.Author.Id,
            new(plan.Id,plan.Revision,"cancel","owner-cannot-cancel")));
        plan=await f.Service.ControlAsync(f.Org,f.Author.Id,new(plan.Id,plan.Revision,"resume","owner-amendment-resume"));
        Assert.Equal("Active",plan.Status);Assert.Equal(f.Manager.Id,plan.ManagerOrganizationUserId);
        Assert.All(await f.Db.WorkDeliveryMutationReceipts.Where(r=>r.IdempotencyKey.StartsWith("owner-amendment")).ToListAsync(),r=>Assert.Equal(f.Author.Id,r.ActorId));
    }
    [Fact]
    public async Task AggregateEvidenceUsesZipByteDigestInsteadOfFileManifestDigest()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        var execution=(await f.Read()).Executions.Single(x=>x.Scope=="Story");
        var entry=execution.Candidate!.Repositories.Single();
        var response=await f.Service.ReadEvidenceAsync(f.Org,f.Qa.Id,new(f.PlanId,execution.Id,entry.RepositoryId));
        Assert.Equal(entry.CandidateCommitSha,response.CommitSha);
        Assert.Equal(new byte[]{1,2},response.Archive);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(response.Archive!)),response.ArchiveSha256);
        Assert.NotEqual("file-manifest-digest",response.ArchiveSha256);
    }

    [Theory]
    [InlineData("Artifact", true, true, true)]
    [InlineData("Code", true, true, false)]
    [InlineData("Artifact", false, true, false)]
    [InlineData("Artifact", true, false, false)]
    public async Task DraftQaReportProposalsResolveInitializationOrderButCannotAuthorizeExecution(string kind, bool managerReview, bool matchingAuthor, bool allowed)
    {
        await using var f = await Fixture.Create(true);
        var author = await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "development");
        author.OrganizationUserId = f.Qa.Id;
        author.RequirementsJson = JsonSerializer.Serialize(new WorkAssignmentRequirements("game-quality-assurance", [], [], []), WorkDeliveryService.Json);
        f.Db.Remove(await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "quality"));
        f.Task.PlanningSpecificationJson = JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Report"], ["Verified"]) { DeliveryKind = kind }, WorkDeliveryService.Json);
        await f.Db.SaveChangesAsync();
        var request = f.Request() with { TaskAssignments = [new(f.Task.Id,
            [new("development", "Human", matchingAuthor ? f.Qa.Id : f.Author.Id) { Requirements = new("game-quality-assurance", [], [], []) },
             new("quality", "Human", managerReview ? f.Manager.Id : f.Qa.Id)])] };
        if (!allowed)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Configure(request));
            Assert.Contains(f.Task.Title, error.Message); Assert.Contains("separate quality review", error.Message);
            Assert.Empty(await f.Db.WorkDeliveryPlans.ToListAsync()); return;
        }
        var draft = await f.Configure(request);
        Assert.Equal("Draft", draft.Status); Assert.Empty(draft.Executions);
        var delivery = f.Task.DeliverySpecificationJson;
        f.Task.DeliverySpecificationJson = null; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Activate(draft));
        f.Task.DeliverySpecificationJson = delivery; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Activate(draft));
        f.Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, WorkItemId = f.Task.Id,
            StageKey = "quality", PrincipalKind = WorkOrchestrationPrincipalKind.Human, OrganizationUserId = f.Manager.Id });
        await f.Db.SaveChangesAsync();
        Assert.Equal("Active", (await f.Activate(draft)).Status);
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("expired", false)]
    [InlineData("generation-changed", false)]
    [InlineData("replaced-application", false)]
    public async Task AcceptedComputeEvidenceMustStillDescribeTheCurrentLiveCandidate(string condition, bool valid)
    {
        await using var f = await Fixture.Create(false);
        var declared=f.Request();
        declared=declared with {TicketWorkflows=declared.TicketWorkflows.Select(w=>w.Scope=="Story"?w with {
            Steps=[new("build-readiness","build-readiness",new("build-readiness","Human",f.Technical.Id)),..w.Steps] }:w).ToArray()};
        await f.Activate(await f.Configure(declared));await f.CompleteTasks();await f.Service.PulseAsync();
        var plan = await f.Db.WorkDeliveryPlans.Include(x => x.Executions).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts).SingleAsync();
        var execution = plan.Executions.First(x => x.CandidateJson != null);
        var candidate = JsonSerializer.Deserialize<WorkDeliveryCandidate>(execution.CandidateJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var installation = Guid.NewGuid(); var environment = Guid.NewGuid(); var command = Guid.NewGuid();
        var proof = new WorkDeliveryComputeEvidence(candidate.Repositories[0].RepositoryId, candidate.Repositories[0].CandidateCommitSha,
            new string('a',64), environment,1,command,Guid.NewGuid(),"http://127.0.0.1:1234/",
            DateTimeOffset.UtcNow.AddHours(condition=="expired" ? -1 : 1));
        f.Db.Add(new CSweet.Domain.Compute.ComputeEnvironment { Id=environment,OrganizationId=f.Org,InstallationId=installation,
            ProviderId="test-provider",ProviderNodeId=Guid.NewGuid(),
            WorkstreamId=f.Project,Generation=condition=="generation-changed" ? 2 : 1,
            State=CSweet.Domain.Compute.ComputeLifecycleState.Ready,LeaseExpiresAt=DateTimeOffset.MaxValue });
        var now=DateTimeOffset.UtcNow;
        f.Db.Add(new CSweet.Domain.Compute.ComputeOperation { Id=command,OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment,
            Action="compute.execute.v1",WorkloadJson="csweet-candidate:exact",CreatedAt=now,Status="Completed" });
        if(condition=="replaced-application")
            f.Db.Add(new CSweet.Domain.Compute.ComputeOperation { Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment,
                Action="compute.execute.v1",WorkloadJson="csweet-candidate:replacement",CreatedAt=now.AddSeconds(1),Status="Pending" });
        var stage=new WorkStageExecution { Id=Guid.NewGuid(),StageKey="build-readiness",AgentInstallationId=installation,
            Status=WorkStageExecutionStatus.Completed,LastOutcomeCode="approved" };
        var attempt=new WorkExecutionAttempt { Id=Guid.NewGuid(),StageExecutionId=stage.Id,Status=WorkExecutionAttemptStatus.Completed };
        var review=new WorkDeliveryReviewResult(candidate.Digest,true,"Ready",[],[]) { ComputeTests=[proof] };
        attempt.ResultJson=JsonSerializer.Serialize(new WorkExecutionOutcomeV1(stage.Id,attempt.Id,"Completed","approved","Ready",
            JsonSerializer.SerializeToElement(review,new JsonSerializerOptions(JsonSerializerDefaults.Web)),[],[]),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        stage.Attempts.Add(attempt); execution.Stages.Add(stage); f.Db.Add(stage); await f.Db.SaveChangesAsync();
        if(valid) await f.Service.RequireCandidateCurrentAsync(plan,execution,default);
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.RequireCandidateCurrentAsync(plan,execution,default));
    }

    [Fact]
    public async Task ComputeTestActivatesWithoutCertificationButCannotApproveWithoutReceipts()
    {
        await using var f = await Fixture.Create(false);
        var request = f.Request();
        request = request with {
            Branches = request.Branches.Select(b => b.Scope == "Release" ? b with { ReadinessMode = "compute-test" } : b).ToArray(),
            TicketWorkflows = request.TicketWorkflows.Select(w=>w.Scope=="Release"?w with {
                Steps=[new("build-readiness","build-readiness",new("build-readiness","Human",f.Technical.Id)),..w.Steps] }:w).ToArray() };
        var draft = await f.Configure(request); await f.Activate(draft); await f.CompleteTasks();
        WorkDeliveryExecutionResponse? release = null;
        for (var pass = 0; pass < 20; pass++) {
            await f.Service.PulseAsync(); var plan = await f.Read();
            release = plan.Executions.FirstOrDefault(x => x.Scope == "Release" && x.CurrentStageKey == "build-readiness");
            if (release is not null) break;
            var review = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
            if (review is not null) { await f.Approve(review); continue; }
            var acceptance = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval");
            if (acceptance is not null) await f.Accept(acceptance);
        }
        Assert.NotNull(release);
        var complete = f.Review(release!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteReviewAsync(f.Org, f.Technical.Id, complete));
        var rejected = complete with { IdempotencyKey = "compute-remediation",
            Result = complete.Result with { Approved = false, Summary = "Runnable test package missing",
                Criteria = [new("Requirement works", false, "Developer must supply a runnable package")], Findings = ["Supply runnable package"] } };
        var blocked = await f.Service.CompleteReviewAsync(f.Org, f.Technical.Id, rejected);
        Assert.Single(blocked.Findings);
        Assert.Contains(blocked.Scopes.Single(x => x.Scope == "Release").Stages, x => x.StageKey == "quality");
        Assert.Contains(blocked.Scopes.Single(x => x.Scope == "Release").Stages, x => x.StageKey == "technical-review");
    }

    [Theory]
    [InlineData("Artifact", "game-quality-assurance", true, true)]
    [InlineData("Artifact", "software-qa", true, true)]
    [InlineData("Code", "game-quality-assurance", true, false)]
    [InlineData("Artifact", "game-engineer", true, false)]
    [InlineData("Artifact", "game-quality-assurance", false, false)]
    public async Task AggregateQaCanOwnOnlyManagerReviewedQaEvidence(string kind, string role, bool managerReviews, bool allowed)
    {
        await using var f = await Fixture.Create(true);
        var author = await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "development");
        author.OrganizationUserId = f.Qa.Id;
        author.RequirementsJson = JsonSerializer.Serialize(new WorkAssignmentRequirements(role, [], [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var review = await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "quality");
        review.OrganizationUserId = managerReviews ? f.Manager.Id : f.Qa.Id;
        f.Task.DeliverySpecificationJson = JsonSerializer.Serialize(new WorkItemDeliverySpecification(Guid.Empty, ["Deliver requirement"], ["Requirement works"])
            { DeliveryKind = kind }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();
        if (allowed) Assert.NotNull(await f.Configure());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => f.Configure());
    }

    [Fact]
    public async Task FollowupTicketsWaitForRemediationWithoutMigratingCompletedSourceWork()
    {
        await using var f=await Fixture.Create(false);
        await f.Activate(await f.Configure());await f.CompleteTasks();await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single(e=>e.Scope=="Story"));await f.Service.PulseAsync();
        var original=await f.Read();var receipt=original.Executions.Single(e=>e.Scope=="Story").Promotions.Single();
        await f.Service.ControlAsync(f.Org,f.Manager.Id,new(original.Id,original.Revision,"cancel","cancel-old"));
        var remediation=new WorkTask {Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,Title="Fix finding",Status=WorkTaskStatus.Ready};
        var workflow=new WorkDeliveryTicketWorkflow(Guid.NewGuid(),[new("assess-fix","review",new("assess-fix","Human",f.Technical.Id))]);
        var ticket=new WorkTask {Id=workflow.TicketId,OrganizationId=f.Org,BoardId=f.Board.Id,Kind=WorkItemKind.Task,
            TypeKey=WorkItemTypeKeys.WorkflowTicketV1,Title="Assess remediation",PlanningSpecificationJson=JsonSerializer.Serialize(
                new WorkItemPlanningSpecification(["Deliver requirement"],["Requirement works"]) {DependencyItemIds=[remediation.Id],Workflow=workflow.Definition()},new JsonSerializerOptions(JsonSerializerDefaults.Web))};
        f.Db.AddRange(remediation,ticket);await f.Db.SaveChangesAsync();
        var plan=await f.Service.ConfigureAsync(f.Org,f.Manager.Id,new(f.Project,"Followup",f.Manager.Id,[],[],[],"followup") {TicketWorkflows=[workflow]});
        f.PlanId=plan.Id;await f.Service.ControlAsync(f.Org,f.Manager.Id,new(plan.Id,plan.Revision,"activate","activate-followup"));await f.Service.PulseAsync();Assert.Empty((await f.Read()).Executions.Single().Stages);
        remediation.Status=WorkTaskStatus.Completed;await f.Db.SaveChangesAsync();await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single());await f.Service.PulseAsync();
        Assert.Equal("Completed",(await f.Read()).Status);
        Assert.Equal(original.Id,JsonSerializer.Deserialize<WorkItemDeliverySpecification>(f.Task.DeliverySpecificationJson!,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.DeliveryPlanId);
        Assert.Equal(receipt,(await f.Service.ReadAsync(f.Org,f.Manager.Id,new(f.Project,original.Id))).Single().Executions.Single(e=>e.Scope=="Story").Promotions.Single());
    }
    [Fact]
    public async Task FlatCrossBoardWorkflowRequiresExplicitBoardAndRepositoryAccess()
    {
        await using var f=await Fixture.Create(false,twoRepositories:true);
        var board=new WorkBoard {Id=Guid.NewGuid(),OrganizationId=f.Org,WorkstreamId=f.Project,Name="Other team",Key="OTHER",ManagerOrganizationUserId=f.Manager.Id};
        f.Db.Add(board);
        var first=new WorkDeliveryTicketWorkflow(Guid.NewGuid(),[new("first","review",new("first","Human",f.Technical.Id))]);
        var last=new WorkDeliveryTicketWorkflow(Guid.NewGuid(),[new("last","review",new("last","Human",f.Technical.Id))]);
        foreach(var pair in new[]{(Workflow:first,Board:f.Board.Id,Dependencies:Array.Empty<Guid>()),(Workflow:last,Board:board.Id,Dependencies:new[]{first.TicketId})})
            f.Db.Add(new WorkTask {Id=pair.Workflow.TicketId,OrganizationId=f.Org,BoardId=pair.Board,Kind=WorkItemKind.Task,TypeKey=WorkItemTypeKeys.WorkflowTicketV1,
                Title="Declared assessment",PlanningSpecificationJson=JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Deliver requirement"],["Requirement works"]) {
                    DependencyItemIds=pair.Dependencies,Workflow=pair.Workflow.Definition()},new JsonSerializerOptions(JsonSerializerDefaults.Web))});
        var repo=f.Repositories[1];var policies=await f.Db.TeamRepositoryPolicies.Where(p=>p.RepositoryId==repo).ToListAsync();
        f.Db.RemoveRange(policies);await f.Db.SaveChangesAsync();
        var request=new ConfigureWorkDeliveryPlanRequest(f.Project,"Flat workflow",f.Manager.Id,[],[new(repo,"Ticket",last.TicketId,"main","main")],[],"cross-board") {TicketWorkflows=[first,last]};
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,request));
        foreach(var person in new[]{f.Manager,f.Technical})foreach(var action in new[]{CSweet.Contracts.WorkManagement.WorkItemActions.Read,CSweet.Contracts.WorkManagement.WorkBoardActions.Configure})
            f.Db.Add(new ScopedActionGrant {Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.OrganizationUser,SubjectId=person.Id,ScopeKind=GrantScopeKind.Board,ScopeId=board.Id,Action=action});
        await f.Db.SaveChangesAsync();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,request));
        foreach(var policy in policies)f.Db.Add(policy);await f.Db.SaveChangesAsync();
        var plan=await f.Service.ConfigureAsync(f.Org,f.Manager.Id,request);f.PlanId=plan.Id;await f.Activate(plan);await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single(e=>e.WorkflowTicketId==first.TicketId));await f.Service.PulseAsync();
        await f.Approve((await f.Read()).Executions.Single(e=>e.WorkflowTicketId==last.TicketId));await f.Service.PulseAsync();
        Assert.Equal("Completed",(await f.Read()).Status);Assert.Equal(2,(await f.Read()).Executions.Count);
    }
    [Fact]
    public async Task ActivatedGraphRequiresFollowupAndCancellationRevokesDocumentAccess()
    {
        await using var f=await Fixture.Create(true);
        await f.Activate(await f.Configure());await f.CompleteTasks();await f.Service.PulseAsync();
        var plan=await f.Read();var story=plan.Executions.Single(e=>e.Scope=="Story");
        Assert.NotEmpty(await f.Db.ScopedActionGrants.Where(g=>g.GrantedBySubjectId==story.Id&&g.RevokedAt==null).ToListAsync());
        plan=await f.Service.ControlAsync(f.Org,f.Manager.Id,new(plan.Id,plan.Revision,"pause","pause"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ConfigureAsync(f.Org,f.Manager.Id,f.Request() with {
            PlanId=plan.Id,ExpectedRevision=plan.Revision,IdempotencyKey="no-adoption" }));
        plan=await f.Service.ControlAsync(f.Org,f.Manager.Id,new(plan.Id,plan.Revision,"cancel","cancel"));
        Assert.All(plan.Executions,e=>Assert.Equal("Cancelled",e.Status));
        Assert.Empty(await f.Db.ScopedActionGrants.Where(g=>g.GrantedBySubjectId==story.Id&&g.RevokedAt==null).ToListAsync());
        Assert.Equal(WorkTaskStatus.Completed,f.Task.Status);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>f.Approve(story));
    }
    [Fact]
    public async Task ActivatedTaskRejectsChangedStaffingUntilScopeIsAmended()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure());
        await WorkDeliveryTaskAuthorization.RequireAsync(f.Db, f.Task, default);
        var qa = await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "quality");
        qa.OrganizationUserId = f.Technical.Id;
        await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkDeliveryTaskAuthorization.RequireAsync(f.Db, f.Task, default));
        Assert.Contains("staffing changed", error.Message);
    }

    [Fact]
    public async Task ArtifactCandidateProvidesAuthorizedExactContentAndRevokesTemporaryAccessAfterReview()
    {
        await using var f = await Fixture.Create(true);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        var evidence = await f.Service.ReadEvidenceAsync(f.Org, f.Qa.Id, new(f.PlanId, story.Id));
        Assert.Equal("Exact artifact", Assert.Single(evidence.Documents).Content);
        Assert.Single(evidence.Children);
        await f.Approve(story);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadEvidenceAsync(f.Org, f.Qa.Id, new(f.PlanId, story.Id)));
    }

    [Fact]
    public async Task LostDatabasePromotionReceiptRecoversNativeReceiptWithoutMergingTwice()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        await f.Approve(story);
        var candidate = story.Candidate!.Repositories.Single();
        await f.Client.DeliveryBranchAsync(new(f.Org, candidate.RepositoryId, "InternalGit", null, "", "", "promote",
            candidate.SourceBranch, candidate.TargetBranch, $"promote:{story.Id:N}:{candidate.RepositoryId:N}:{story.Candidate.Digest}",
            candidate.SourceCommitSha, candidate.TargetCommitSha, candidate.CandidateCommitSha));
        await f.Service.PulseAsync();
        story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        Assert.Equal("Completed", story.Status);
        Assert.Single(story.Promotions.Where(x => x.Status == "Completed"));
        Assert.Equal(1, f.Host.SuccessfulPromotions[candidate.RepositoryId]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPlanUsesIndependentTaskQaAndAggregateAcceptanceWithoutSprintIdentities(bool artifacts)
    {
        await using var f = await Fixture.Create(artifacts);
        var draft = await f.Configure();
        var active = await f.Activate(draft);
        Assert.Equal(3, active.Executions.Count);
        Assert.Empty(active.Executions.SelectMany(x => x.Stages));
        await f.Service.PulseAsync();
        Assert.All((await f.Read()).Executions, x => Assert.Equal("WaitingForChildren", x.Status));
        await f.CompleteTasks();
        await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        Assert.Equal("WaitingForHuman", story.Status);
        if (artifacts) Assert.Single(story.Candidate!.Documents); else Assert.Single(story.Candidate!.Repositories);
        await f.Approve(story);
        await f.Service.PulseAsync();
        var epic = (await f.Read()).Executions.Single(x => x.Scope == "Epic");
        Assert.True(epic.Stages.Count>0,epic.Status+": "+epic.BlockedReason+"; story="+(await f.Read()).Executions.Single(e=>e.Scope=="Story").Status);
        await f.Approve(epic);
        epic = (await f.Read()).Executions.Single(x => x.Scope == "Epic");
        Assert.Equal("WaitingForApproval", epic.Status);
        await f.Accept(epic);
        await f.Service.PulseAsync();
        var release = (await f.Read()).Executions.Single(x => x.Scope == "Release");
        await f.Approve(release);
        await f.Approve((await f.Read()).Executions.Single(x => x.Scope == "Release"));
        release = (await f.Read()).Executions.Single(x => x.Scope == "Release");
        Assert.Equal("WaitingForApproval", release.Status);
        await f.Accept(release);
        await f.Service.PulseAsync();
        Assert.Equal("Completed", (await f.Read()).Status);
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        Assert.Equal(WorkTaskStatus.Completed, f.Story.Status);
        Assert.Equal(WorkTaskStatus.Completed, f.Epic.Status);
        Assert.NotEmpty(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == WorkDeliveryCapabilities.Changed).ToListAsync());
        if (artifacts) Assert.Equal(0, f.Host.BranchCalls);
    }

    [Fact]
    public async Task ScopeStaffingAndCandidateChangesCannotReuseReviewAndAuthorCannotQa()
    {
        await using var f = await Fixture.Create(false);
        var request = f.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfigureAsync(f.Org, f.Manager.Id,
            f.Declare(request with { TicketWorkflows=request.TicketWorkflows.Select(w=>w.Scope=="Story"?w with {
                Steps=[new("quality","quality",new("quality","Human",f.Author.Id))] }:w).ToArray() })));
        f.Declare(request);f.Db.ChangeTracker.Clear();
        var draft = await f.Configure(); await f.Activate(draft); await f.CompleteTasks(); await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteReviewAsync(f.Org, f.Author.Id, f.Review(story)));
        f.Host.ChangeTarget(story.Candidate!.Repositories.Single());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Approve(story));
        await f.Service.PulseAsync();
        Assert.Equal("Blocked", (await f.Read()).Executions.Single(x => x.Scope == "Story").Status);
    }

    [Fact]
    public async Task PartialPromotionPersistsReceiptsAndResumesOnlyUnfinishedRepositories()
    {
        await using var f = await Fixture.Create(false, twoRepositories: true);
        await f.Activate(await f.Configure()); await f.CompleteTasks();
        // Exercise aggregate sequencing with the complete two-repository candidate.
        for (var count = 0; count < 10; count++)
        {
            await f.Service.PulseAsync();
            var plan = await f.Read();
            var review = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
            if (review is not null) { await f.Approve(review); continue; }
            var acceptance = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval");
            if (acceptance is null) continue;
            if (acceptance.Scope == "Release") { f.Host.FailRepository = acceptance.Candidate!.Repositories.Last().RepositoryId; await f.Accept(acceptance); break; }
            await f.Accept(acceptance);
        }
        await f.Service.PulseAsync();
        var stopped = (await f.Read()).Executions.Single(x => x.Scope == "Release");
        Assert.Equal("PartiallyPromoted", stopped.Status);
        Assert.Single(stopped.Promotions.Where(x => x.Status == "Completed"));
        var success = stopped.Promotions.Single(x => x.Status == "Completed").RepositoryId;
        var successfulCalls = f.Host.SuccessfulPromotions[success];
        f.Host.FailRepository = null;
        await f.Service.RecoverAsync(f.Org, f.Manager.Id, new(f.PlanId, stopped.Id, stopped.Revision, "recover", "Provider restored"));
        await f.Service.PulseAsync();
        Assert.Equal("Completed", (await f.Read()).Status);
        Assert.Equal(successfulCalls, f.Host.SuccessfulPromotions[success]);
    }

    [Fact]
    public async Task PauseCancelIdempotencyAndManualDoneCannotBypassAuthority()
    {
        await using var f = await Fixture.Create(true);
        var draft = await f.Configure();
        Assert.Equal(draft.Id, (await f.Configure()).Id);
        await f.Activate(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkDeliveryTaskAuthorization.PreventManualCompletionAsync(f.Db, f.Task, default));
        var active = await f.Read();
        await f.Service.ControlAsync(f.Org, f.Manager.Id, new(active.Id, active.Revision, "pause", "pause"));
        await f.CompleteTasks(); await f.Service.PulseAsync();
        Assert.Empty((await f.Read()).Executions.SelectMany(x => x.Stages));
        var paused = await f.Read();
        await f.Service.ControlAsync(f.Org, f.Manager.Id, new(paused.Id, paused.Revision, "cancel", "cancel"));
        Assert.All((await f.Read()).Executions, x => Assert.Equal("Cancelled", x.Status));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; }
        public Guid Org = Guid.NewGuid(), Project = Guid.NewGuid(), PlanId;
        public OrganizationUser Manager = null!, Author = null!, Qa = null!, Technical = null!;
        public WorkBoard Board = null!;
        public WorkTask Task = null!, Story = null!, Epic = null!;
        public readonly List<Guid> Repositories = [];
        public readonly bool Artifact;
        public ITrustedSourceControlHostClient Client = DispatchProxy.Create<ITrustedSourceControlHostClient, HostProxy>();
        public HostProxy Host => (HostProxy)Client;
        public WorkDeliveryService Service => new(Db, new ScopedActionAuthorizationService(Db), Client, TimeProvider.System);
        static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Fixture(bool artifact, DbContextOptions<CSweetDbContext>? options = null)
        { Artifact = artifact; Db = new(options ?? new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options); }
        public static async Task<Fixture> Create(bool artifact, bool twoRepositories = false)
        { var f = new Fixture(artifact); await f.Seed(twoRepositories); return f; }
        public async Task Seed(bool twoRepositories = false)
        {
            Db.CoreOrganizations.Add(new() { Id = Org, Name = "Fresh hierarchical project" });
            OrganizationUser Person(string name) => new() { Id = Guid.NewGuid(), OrganizationId = Org, DisplayName = name, IsActive = true, EmployeeType = EmployeeType.Human };
            Manager = Person("Manager"); Author = Person("Author"); Qa = Person("Independent QA"); Technical = Person("Technical reviewer");
            Db.CoreOrganizationUsers.AddRange(Manager, Author, Qa, Technical);
            Db.Workstreams.Add(new() { Id = Project, OrganizationId = Org, Name = "Release", Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = Manager.Id });
            var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = Org, Name = "Delivery", NormalizedName = "DELIVERY", TeamKey = "delivery", LeadOrganizationUserId = Manager.Id };
            Db.OrganizationTeams.Add(team);
            Board = new() { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Project, TeamId = team.Id,
                ManagerOrganizationUserId = Manager.Id, Name = "Team", Key = "TEAM", ProfileKey = WorkBoardProfileKeys.SoftwareDeliveryV2 };
            Db.WorkBoards.Add(Board);
            foreach (var person in new[] { Manager, Author, Qa, Technical })
            {
                Db.ProjectParticipants.Add(new() { OrganizationId = Org, WorkstreamId = Project, OrganizationUserId = person.Id, AddedByOrganizationUserId = Manager.Id });
                Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, OrganizationUserId = person.Id, TeamId = team.Id });
                foreach (var action in WorkDeliveryCapabilities.All)
                    Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, SubjectKind = GrantSubjectKind.OrganizationUser,
                        SubjectId = person.Id, ScopeKind = GrantScopeKind.Workstream, ScopeId = Project, Action = action });
                foreach (var action in new[] { CSweet.Contracts.WorkManagement.WorkItemActions.Read, CSweet.Contracts.WorkManagement.WorkBoardActions.Configure })
                    Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, SubjectKind = GrantSubjectKind.OrganizationUser,
                        SubjectId = person.Id, ScopeKind = GrantScopeKind.Board, ScopeId = Board.Id, Action = action });
            }
            WorkTask Item(WorkItemKind kind, bool executable, Guid? parent) => new() { Id = Guid.NewGuid(), OrganizationId = Org,
                BoardId = Board.Id, Kind = kind, IsExecutable = executable, ParentWorkTaskId = parent, Title = kind.ToString(), PlanningRevision = 1,
                PlanningSpecificationJson = JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Deliver requirement"], ["Requirement works"]), Json) };
            Epic = Item(WorkItemKind.Epic, false, null); Story = Item(WorkItemKind.Story, false, Epic.Id); Task = Item(WorkItemKind.Task, true, Story.Id);
            Db.CoreWorkTasks.AddRange(Epic, Story, Task);
            void Assign(string key, OrganizationUser person) => Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org,
                BoardId = Board.Id, WorkItemId = Task.Id, StageKey = key, PrincipalKind = WorkOrchestrationPrincipalKind.Human, OrganizationUserId = person.Id });
            Assign("development", Author); Assign("quality", Qa);
            if (!Artifact)
            {
                Assign("technical-review", Technical);
                Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, BoardId = Board.Id, WorkItemId = Task.Id,
                    StageKey = "task-integration", PrincipalKind = WorkOrchestrationPrincipalKind.PlatformAction, PlatformAction = HierarchicalWorkflows.TaskIntegrationAction });
                for (var index = 0; index < (twoRepositories ? 2 : 1); index++)
                {
                    var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = Org, Provider = SourceControlProvider.InternalGit, Status = SourceControlConnectionStatus.Connected };
                    var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = Org, Connection = connection, ConnectionId = connection.Id,
                        Name = "Repository " + index, DefaultBranch = "main", Status = SourceControlRepositoryStatus.Ready };
                    Db.SourceControlRepositories.Add(repository); Repositories.Add(repository.Id);
                    Db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, TeamId = team.Id, RepositoryId = repository.Id });
                }
            }
            await Db.SaveChangesAsync();
        }
        // This fixture is one agent-authored plan. The platform must never synthesize these review steps.
        private readonly Guid storyTicket = Guid.NewGuid(), epicTicket = Guid.NewGuid(), releaseTicket = Guid.NewGuid();
        public ConfigureWorkDeliveryPlanRequest Request()
        {
            WorkDeliveryTicketStep Step(string key,string handler,Guid person) => new(key,handler,new(key,"Human",person));
            var workflows = new WorkDeliveryTicketWorkflow[] {
                new(storyTicket,[Step("quality","quality",Qa.Id)]) { Scope="Story",ScopeItemId=Story.Id,EvidenceItemIds=[Task.Id],
                    PromoteOnCompletion=!Artifact,CompleteContainerItemIds=[Story.Id] },
                new(epicTicket,[Step("technical-review","source-review",Technical.Id),Step("manager-review","acceptance",Manager.Id)]) {
                    Scope="Epic",ScopeItemId=Epic.Id,EvidenceItemIds=[storyTicket],CompleteContainerItemIds=[Epic.Id] },
                new(releaseTicket,[Step("quality","quality",Qa.Id),Step("technical-review","source-review",Technical.Id),Step("manager-review","acceptance",Manager.Id)]) {
                    Scope="Release",EvidenceItemIds=[epicTicket],PromoteOnCompletion=!Artifact }
            };
            for(var i=0;i<workflows.Length;i++) {
                var workflow=workflows[i];
                if(Db.CoreWorkTasks.Any(t=>t.Id==workflow.TicketId))continue;
                Db.CoreWorkTasks.Add(new() {Id=workflow.TicketId,OrganizationId=Org,BoardId=Board.Id,Kind=WorkItemKind.Task,
                    TypeKey=WorkItemTypeKeys.WorkflowTicketV1,IsExecutable=false,Title="Declared "+workflow.Scope+" assessment",
                    PlanningSpecificationJson=JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Deliver requirement"],["Requirement works"]) {
                        DependencyItemIds=i==0?[Task.Id]:[workflows[i-1].TicketId],Workflow=workflow.Definition() },Json)});
            }
            Db.SaveChanges();
            return new(Project,"Initial release",Manager.Id,[Epic.Id],
                Repositories.SelectMany(id=>new[] {new WorkDeliveryBranchBinding(id,"Release",null,"csweet/release/initial","main"),
                    new WorkDeliveryBranchBinding(id,"Story",Story.Id,"csweet/story/one","csweet/release/initial"),
                    new WorkDeliveryBranchBinding(id,"Epic",Epic.Id,"csweet/release/initial","csweet/release/initial")}).ToArray(),
                workflows.Select(w=>new WorkDeliveryScopeAssignment(w.Scope,w.ScopeItemId,Board.Id,w.Steps.Select(s=>s.Assignment).ToArray())).ToArray(),"configure") {
                    TicketWorkflows=workflows };
        }
        public ConfigureWorkDeliveryPlanRequest Declare(ConfigureWorkDeliveryPlanRequest request)
        {
            foreach(var workflow in request.TicketWorkflows) {
                var ticket=Db.CoreWorkTasks.Single(t=>t.Id==workflow.TicketId);
                var planning=JsonSerializer.Deserialize<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!,Json)!;
                ticket.PlanningSpecificationJson=JsonSerializer.Serialize(planning with {Workflow=workflow.Definition()},Json);
            }
            Db.SaveChanges();return request;
        }
        public async Task<WorkDeliveryPlanResponse> Configure(ConfigureWorkDeliveryPlanRequest? request = null)
        {
            var result = await Service.ConfigureAsync(Org, Manager.Id, Declare(request ?? Request())); PlanId = result.Id;
            Task = await Db.CoreWorkTasks.SingleAsync(x => x.Id == Task.Id);
            Task.DeliverySpecificationJson = JsonSerializer.Serialize(new WorkItemDeliverySpecification(Artifact ? Guid.Empty : Repositories[0],
                ["Deliver requirement"], ["Requirement works"]) { BaseBranch = Artifact ? "" : "csweet/story/one", DeliveryKind = Artifact ? "Artifact" : "Code", DeliveryPlanId = PlanId }, Json);
            await Db.SaveChangesAsync(); return result;
        }
        public Task<WorkDeliveryPlanResponse> Activate(WorkDeliveryPlanResponse draft) => Service.ControlAsync(Org, Manager.Id, new(draft.Id, draft.Revision, "activate", "activate"));
        public async Task<WorkDeliveryPlanResponse> Read() => (await Service.ReadAsync(Org, Manager.Id, new(Project, PlanId))).Single();
        public async Task CompleteTasks()
        {
            Task.Status = WorkTaskStatus.Completed;
            var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = Task.Id, Status = WorkItemExecutionStatus.Completed, CreatedAt = DateTimeOffset.UtcNow };
            execution.Stages.Add(new() { Id = Guid.NewGuid(), StageKey = "quality", Status = WorkStageExecutionStatus.Completed, LastOutcomeCode = "passed", CompletedAt = DateTimeOffset.UtcNow });
            if (Artifact)
            {
                var artifact = Guid.NewGuid(); var revision = Guid.NewGuid();
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Exact artifact")));
                Db.CoreArtifacts.Add(new() { Id = artifact, OrganizationId = Org, OriginWorkItemId = Task.Id, WorkstreamId = Project,
                    TeamId = Board.TeamId, CreatedByOrganizationUserId = Author.Id, LatestRevisionId = revision, Title = "Delivered artifact" });
                Db.ArtifactRevisions.Add(new() { Id = revision, ArtifactId = artifact, OrganizationId = Org, ContentSha256 = sha,
                    CreatedByOrganizationUserId = Author.Id, Content = "Exact artifact", Number = 1 });
                var author = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "development", Status = WorkStageExecutionStatus.Completed };
                var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Completed, CreatedAt = DateTimeOffset.UtcNow };
                attempt.ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(author.Id, attempt.Id, "completed", "artifact-delivered", "Delivered exact artifact",
                    JsonSerializer.SerializeToElement(new { artifactId = artifact, revisionId = revision, sha256 = sha }), [], []), Json);
                author.Attempts.Add(attempt); execution.Stages.Add(author);
            }
            Db.WorkItemExecutions.Add(execution); await Db.SaveChangesAsync();
        }
        public CompleteWorkDeliveryReviewRequest Review(WorkDeliveryExecutionResponse execution) => new(PlanId, execution.Id, execution.Stages.Last().Id,
            execution.Revision, new(execution.Candidate!.Digest, true, "Verified exact candidate", [new("Requirement works", true, "Observed against exact content")], [])
                { Validations = execution.Candidate.Repositories.Select(x => new WorkDeliveryValidationEvidence(x.RepositoryId, x.CandidateCommitSha, "regression", 0, true, "Passed")).ToArray() }, Guid.NewGuid().ToString("N"));
        public async Task Approve(WorkDeliveryExecutionResponse execution) => await Service.CompleteReviewAsync(Org,
            execution.Stages.Last().StageKey == "quality" ? Qa.Id : Technical.Id, Review(execution));
        public async Task Accept(WorkDeliveryExecutionResponse execution) => await Service.AcceptAsync(Org, Manager.Id,
            new(PlanId, execution.Id, execution.Revision, execution.Candidate!.Digest, true, "Manager accepts tested integration", [new("Requirement works", true, "Reviewed QA and technical evidence")], [], Guid.NewGuid().ToString("N")));
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    public class HostProxy : DispatchProxy
    {
        public int BranchCalls; public Guid? FailRepository;
        public bool IntegrationConflictOnce;
        public byte[] SnapshotArchive = [1,2];
        public int CommitHashLength = 64;
        public readonly Dictionary<Guid, int> SuccessfulPromotions = [];
        readonly Dictionary<(Guid, string), string> heads = [];
        readonly Dictionary<string, DeliveryBranchResult> receipts = [];
        string Sha(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..CommitHashLength];
        string Head(Guid repo, string branch) => heads.TryGetValue((repo, branch), out var sha) ? sha : heads[(repo, branch)] = Sha(repo + branch);
        public void AdvanceStory(Guid repo, string branch) => heads[(repo, branch)] = Sha("advanced:" + Head(repo, branch));
        public void ChangeTarget(WorkDeliveryRepositoryCandidate entry) => heads[(entry.RepositoryId, entry.TargetBranch)] = Sha("changed");
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if(method!.Name==nameof(ITrustedSourceControlHostClient.PrepareInternalWorkspaceAsync))
            {
                var request=(InternalGitWorkspaceRequest)args![0]!;
                return System.Threading.Tasks.Task.FromResult(new TrustedWorkspaceSnapshot("review",request.ExpectedSha!,false,SnapshotArchive,"file-manifest-digest",1,SnapshotArchive.Length));
            }
            if (method!.Name != nameof(ITrustedSourceControlHostClient.DeliveryBranchAsync)) throw new NotSupportedException(method.Name);
            BranchCalls++; var r = (DeliveryBranchOperation)args![0]!;
            if (r.Operation is "promote" or "receipt" && receipts.TryGetValue(r.IdempotencyKey, out var replay)) return System.Threading.Tasks.Task.FromResult(replay);
            var source = Head(r.RepositoryId, r.SourceBranch); var target = Head(r.RepositoryId, r.TargetBranch);
            DeliveryBranchResult result = new(source, target, r.Operation == "candidate" ? Sha(source + target) : null);
            if (r.Operation == "candidate" && IntegrationConflictOnce)
                return System.Threading.Tasks.Task.FromResult(new DeliveryBranchResult(source, target, null,
                    Error: "Resolve integration conflicts before validation.") { ErrorCode = "integration_conflicts", ConflictPaths = ["shared.txt"] });
            if (r.Operation == "rework" && IntegrationConflictOnce)
            {
                IntegrationConflictOnce = false;
                return System.Threading.Tasks.Task.FromResult(new DeliveryBranchResult(source, target, null)
                    { ErrorCode = "integration_conflicts", ConflictPaths = ["shared.txt"], ReworkCommitSha = Sha("rework" + source + target)[..40] });
            }
            if (r.Operation == "ensure") heads[(r.RepositoryId, r.SourceBranch)] = target;
            if (r.Operation == "promote")
            {
                result = new(source, target, r.CandidateCommitSha, r.RepositoryId != FailRepository,
                    r.RepositoryId == FailRepository ? "Provider failed; retry unfinished repository" : null);
                if (result.Promoted)
                { heads[(r.RepositoryId, r.TargetBranch)] = r.CandidateCommitSha!; receipts[r.IdempotencyKey] = result;
                  SuccessfulPromotions[r.RepositoryId] = SuccessfulPromotions.GetValueOrDefault(r.RepositoryId) + 1; }
            }
            return System.Threading.Tasks.Task.FromResult(result);
        }
    }
}
