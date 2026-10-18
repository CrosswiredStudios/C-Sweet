using System.Reflection;
using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ArtifactRepositoryInputTests
{
    [Theory]
    [InlineData("current", true)]
    [InlineData("paused", false)]
    [InlineData("lease", false)]
    [InlineData("finished", false)]
    [InlineData("reassigned", false)]
    [InlineData("planning", false)]
    [InlineData("grant", false)]
    [InlineData("member", false)]
    [InlineData("connection", false)]
    [InlineData("dependency", false)]
    [InlineData("dependency-reworked", false)]
    public async Task SavedArtifactInputIsNarrowAndReauthorizedOnEveryRead(string change, bool allowed)
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        await f.Activate(await f.Configure());
        var plan = await f.Db.WorkDeliveryPlans.SingleAsync();
        var input = new WorkTask { Id=Guid.NewGuid(), OrganizationId=f.Org,BoardId=f.Board.Id,ParentWorkTaskId=f.Story.Id,
            Kind=WorkItemKind.Task,IsExecutable=true,PlanningRevision=1,AssignmentRevision=1,
            PlanningSpecificationJson=WorkDeliveryService.Encode(new WorkItemPlanningSpecification(["Inspect"],["Observed"]) {DeliveryKind="Artifact",DependencyItemIds=[f.Task.Id]}),
            DeliverySpecificationJson=WorkDeliveryService.Encode(new WorkItemDeliverySpecification(Guid.Empty,["Inspect"],["Observed"])
                {DeliveryKind="Artifact",DeliveryPlanId=plan.Id,DependencyItemIds=[f.Task.Id]}) };
        f.Db.Add(input);
        var installation=Guid.NewGuid(); f.Author.AgentInstallationId=installation;
        f.Db.AgentInstallations.Add(new() {Id=installation,BusinessId=f.Org.ToString(),IsEnabled=true,RevisionStatus=PluginRevisionStatus.Active,SetupState=PluginSetupState.Ready});
        var staffing=new WorkItemStageAssignment {Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,WorkItemId=input.Id,
            StageKey="specialist-execution",PrincipalKind=WorkOrchestrationPrincipalKind.AgentInstallation,OrganizationUserId=f.Author.Id,AgentInstallationId=installation};
        f.Db.Add(staffing);
        var scopes=WorkDeliveryService.Decode<WorkDeliveryScopeSnapshot[]>(plan.ScopesJson).Select(s=>s.ItemId==f.Story.Id?s with {
            ChildIds=s.ChildIds.Append(input.Id).ToArray(),
            ChildPlanningRevisions=s.ChildPlanningRevisions.Append(new KeyValuePair<Guid,long>(input.Id,1)).ToDictionary(x=>x.Key,x=>x.Value),
            ChildDeliveryDigests=s.ChildDeliveryDigests.Append(new KeyValuePair<Guid,string>(input.Id,WorkDeliveryService.TaskDeliveryDigest(input,[staffing]))).ToDictionary(x=>x.Key,x=>x.Value)
        }:s).ToArray();
        plan.ScopesJson=WorkDeliveryService.Encode(scopes);
        f.Task.Status=WorkTaskStatus.Completed;f.Task.MergeStatus="Merged";f.Task.MergeCommitSha=new string('d',40);
        f.Db.Add(new WorkTaskIntegrationReceipt {Id=Guid.NewGuid(),OrganizationId=f.Org,PlanId=plan.Id,WorkItemId=f.Task.Id,
            RepositoryId=f.Repositories[0],Status="Completed",CandidateCommitSha=f.Task.MergeCommitSha});
        var sprint=new WorkSprintExecution {Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,Status=WorkSprintExecutionStatus.Active};
        var execution=new WorkItemExecution {Id=Guid.NewGuid(),WorkItemId=input.Id,SprintExecutionId=sprint.Id,CurrentStageKey="specialist-execution"};
        var stage=new WorkStageExecution {Id=Guid.NewGuid(),ItemExecutionId=execution.Id,StageKey="specialist-execution",Status=WorkStageExecutionStatus.Running,
            OrganizationUserId=f.Author.Id,AgentInstallationId=installation};
        var attempt=new WorkExecutionAttempt {Id=Guid.NewGuid(),StageExecutionId=stage.Id,Attempt=1,Status=WorkExecutionAttemptStatus.Running};
        f.Db.AddRange(sprint,execution,stage,attempt);
        f.Db.ScopedActionGrants.Add(new() {Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.AgentInstallation,SubjectId=installation,
            ScopeKind=GrantScopeKind.WorkItem,ScopeId=input.Id,Action=CSweet.Contracts.WorkManagement.WorkItemActions.Read});
        await f.Db.SaveChangesAsync();
        var client=DispatchProxy.Create<ITrustedSourceControlHostClient, InputHost>(); var host=(InputHost)client;
        var assignment=JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {OrganizationId=f.Org,OrganizationUserId=f.Author.Id,AgentInstallationId=installation,
            ExecutionId=execution.Id,StageExecutionId=stage.Id,AttemptId=attempt.Id,ItemId=input.Id,BoardId=f.Board.Id,PlanningRevision=1,AssignmentRevision=1,
            DeliveryPlanId=plan.Id,ScopeRevision=plan.ScopeRevision,WorkstreamId=f.Project,StageKey=stage.StageKey,Scope="Task",SprintExecutionId=sprint.Id,SprintId=Guid.NewGuid(),PolicyRevisionId=Guid.NewGuid(),
            Item=JsonSerializer.SerializeToElement(new {}),Input=JsonSerializer.SerializeToElement(new {}),PriorOutcomes=[],Evidence=[]};
        assignment=assignment with {Candidate=await WorkArtifactRepositoryInput.PinAsync(f.Db,client,input,assignment,default)};
        Assert.NotNull(assignment.Candidate); Assert.Equal(host.Head,assignment.Candidate.Repositories.Single().CandidateCommitSha);
        var inbox=new AgentWorkInbox(f.Db,new EphemeralDataProtectionProvider(),TimeProvider.System);
        var work=await inbox.EnqueueAsync(f.Org.ToString(),installation,AgentWorkKind.Capability,"work.execution.run.v1",
            JsonSerializer.SerializeToElement(assignment,WorkDeliveryService.Json),"input-test",DateTimeOffset.UtcNow.AddHours(1),"","","WorkStageExecution",stage.Id.ToString("D"));
        work.Status=AgentWorkStatus.Leased;
        var lease=new AgentWorkAttempt {Id=Guid.NewGuid(),AgentWorkItemId=work.Id,LeaseExpiresAt=DateTimeOffset.UtcNow.AddMinutes(10)};
        f.Db.AgentWorkAttempts.Add(lease);
        attempt.AgentWorkItemId=work.Id;await f.Db.SaveChangesAsync();
        var service=new WorkDeliveryService(f.Db,new ScopedActionAuthorizationService(f.Db),client,TimeProvider.System,inbox);
        var request=new ReadWorkDeliveryEvidenceRequest(plan.Id,execution.Id);
        Assert.Equal(assignment.Candidate.Digest,(await service.ReadEvidenceAsync(f.Org,installation,request)).Candidate.Digest);
        // A moving Story never silently repins a durable attempt.
        host.Head=new string('b',40);
        Assert.Equal(new string('a',40),(await service.ReadEvidenceAsync(f.Org,installation,request)).Candidate.Repositories.Single().CandidateCommitSha);
        var snapshot = await service.ReadEvidenceAsync(f.Org,installation,request with { RepositoryId=f.Repositories[0] });
        Assert.Equal(new string('a',40),snapshot.CommitSha); Assert.Equal(new byte[]{1,2},snapshot.Archive);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot.Archive!)),snapshot.ArchiveSha256);
        Assert.NotEqual("file-manifest-digest",snapshot.ArchiveSha256);
        host.WrongCommit=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReadEvidenceAsync(f.Org,installation,request with {RepositoryId=f.Repositories[0]}));
        host.WrongCommit=false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReadEvidenceAsync(f.Org,f.Qa.Id,request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReadEvidenceAsync(f.Org,installation,request with {RepositoryId=Guid.NewGuid()}));
        switch(change)
        {
            case "paused": plan.Status="Paused";break;
            case "lease": work.Attempts.Single().LeaseExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);break;
            case "finished": stage.Status=WorkStageExecutionStatus.Completed;break;
            case "reassigned": stage.AgentInstallationId=Guid.NewGuid();break;
            case "planning": input.PlanningRevision++;break;
            case "grant": (await f.Db.TeamRepositoryPolicies.SingleAsync()).DisabledAt=DateTimeOffset.UtcNow;break;
            case "member": (await f.Db.TeamMemberships.SingleAsync(m=>m.OrganizationUserId==f.Author.Id)).EndedAt=DateTimeOffset.UtcNow;break;
            case "connection": (await f.Db.SourceControlConnections.SingleAsync()).Status=SourceControlConnectionStatus.Disconnected;break;
            case "dependency": f.Task.Status=WorkTaskStatus.Running;break;
            case "dependency-reworked":
                f.Task.MergeCommitSha=new string('e',40);
                f.Db.WorkTaskIntegrationReceipts.Add(new() {Id=Guid.NewGuid(),OrganizationId=f.Org,PlanId=plan.Id,WorkItemId=f.Task.Id,
                    RepositoryId=f.Repositories[0],Status="Completed",CandidateCommitSha=f.Task.MergeCommitSha});
                break;
        }
        await f.Db.SaveChangesAsync();
        if(allowed) Assert.Equal(assignment.Candidate.Digest,(await service.ReadEvidenceAsync(f.Org,installation,request)).Candidate.Digest);
        else Assert.NotNull(await Record.ExceptionAsync(()=>service.ReadEvidenceAsync(f.Org,installation,request)));
        Assert.Equal(1,host.Inspections);
    }
    public class InputHost : DispatchProxy
    {
        public string Head=new('a',40); public int Inspections; public bool WrongCommit;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if(method!.Name==nameof(ITrustedSourceControlHostClient.PrepareInternalWorkspaceAsync))
            {
                var request=(InternalGitWorkspaceRequest)args![0]!;
                Assert.Equal("csweet/story/one",request.Branch); Assert.Equal(new string('a',40),request.ExpectedSha);
                return Task.FromResult(new TrustedWorkspaceSnapshot("input",WrongCommit?Head:request.ExpectedSha!,false,[1,2],"file-manifest-digest",1,2));
            }
            if(method!.Name!=nameof(ITrustedSourceControlHostClient.DeliveryBranchAsync)) throw new NotSupportedException(method.Name);
            Inspections++; return Task.FromResult(new DeliveryBranchResult(Head,new string('c',40),null));
        }
    }
}
