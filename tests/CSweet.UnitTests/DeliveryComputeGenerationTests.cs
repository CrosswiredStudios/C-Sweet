using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using EnvironmentRecord=CSweet.Domain.Compute.ComputeEnvironment;
using OperationRecord=CSweet.Domain.Compute.ComputeOperation;

namespace CSweet.UnitTests;

public sealed class DeliveryComputeGenerationTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Encode<T>(T value)=>JsonSerializer.Serialize(value,Json);

    private static async Task<WorkDeliveryExecutionResponse> ReachReadiness(HierarchicalDeliveryServiceTests.Fixture f)
    {
        f.Host.CommitHashLength=40;
        var request=f.Request();
        request=request with {Branches=request.Branches.Select(b=>b.Scope=="Release"?b with{ReadinessMode="compute-test"}:b).ToArray(),
            TicketWorkflows=request.TicketWorkflows.Select(w=>w.Scope=="Release"?w with{Steps=[new("build-readiness","build-readiness",new("build-readiness","Human",f.Technical.Id)),..w.Steps]}:w).ToArray()};
        await f.Activate(await f.Configure(request));await f.CompleteTasks();
        for(var pass=0;pass<20;pass++) {
            await f.Service.PulseAsync();var plan=await f.Read();
            if(plan.Executions.FirstOrDefault(e=>e.Scope=="Release"&&e.CurrentStageKey=="build-readiness") is {} ready)return ready;
            if(plan.Executions.FirstOrDefault(e=>e.Status=="WaitingForHuman") is {} review)await f.Approve(review);
            else if(plan.Executions.FirstOrDefault(e=>e.Status=="WaitingForApproval") is {} manager)await f.Accept(manager);
        }
        throw new InvalidOperationException("Release readiness not reached");
    }

    [Fact]
    public async Task Manager_or_owner_can_retry_failed_validation_without_importing_acceptance_or_error_specific_rules()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);var ready=await ReachReadiness(f);
        var review=f.Review(ready);await f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,review with {Result=review.Result with {
            Approved=false,Summary="An arbitrary failed install or review",Findings=["Retained failed validation"]}});
        f.Author.PermissionLevel=OrganizationPermissionLevel.Owner;
        var plan=await f.Read();var request=new ControlWorkDeliveryPlanRequest(plan.Id,plan.Revision,"retry-validation","owner-retry","Retry the corrected development path");
        var retried=await f.Service.ControlAsync(f.Org,f.Author.Id,request);
        await f.Service.ControlAsync(f.Org,f.Author.Id,request);
        Assert.Equal("WaitingForHuman",retried.Executions.Single(e=>e.Id==ready.Id).Status);
        Assert.All(retried.Findings,x=>Assert.Equal("Superseded",x.Status));
        Assert.Contains(retried.Executions.Single(e=>e.Id==ready.Id).Stages,s=>s.LastOutcomeCode=="changes_requested");
        await f.Service.PulseAsync();
        Assert.Equal("build-readiness",(await f.Read()).Executions.Single(e=>e.Id==ready.Id).CurrentStageKey);
    }

    [Fact]
    public async Task Retrying_failed_technical_review_keeps_completed_quality_and_readiness()
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        await f.Activate(await f.Configure(f.Request()));await f.CompleteTasks();
        WorkDeliveryExecutionResponse? technical=null;
        for(var pass=0;pass<20;pass++) {
            await f.Service.PulseAsync();var plan=await f.Read();
            technical=plan.Executions.FirstOrDefault(e=>e.Scope=="Release"&&e.CurrentStageKey=="technical-review"&&e.Status=="WaitingForHuman");
            if(technical is not null)break;
            if(plan.Executions.FirstOrDefault(e=>e.Status=="WaitingForHuman") is {} review)await f.Approve(review);
            else if(plan.Executions.FirstOrDefault(e=>e.Status=="WaitingForApproval") is {} manager)await f.Accept(manager);
        }
        Assert.NotNull(technical);var quality=technical.Stages.Last(s=>s.StageKey=="quality");
        var rejected=f.Review(technical);await f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,rejected with {Result=rejected.Result with {
            Approved=false,Summary="Model request interrupted",Findings=["Retry technical review"]}});
        f.Author.PermissionLevel=OrganizationPermissionLevel.Owner;
        var current=await f.Read();var retried=await f.Service.ControlAsync(f.Org,f.Author.Id,
            new(current.Id,current.Revision,"retry-validation","retry-only-technical","Restore the model request"));
        var execution=retried.Executions.Single(e=>e.Id==technical.Id);
        Assert.Equal("technical-review",execution.CurrentStageKey);
        Assert.Equal("WaitingForHuman",execution.Status);
        Assert.Equal("approved",execution.Stages.Single(s=>s.Id==quality.Id).LastOutcomeCode);
        Assert.Equal(technical.Stages.Count+1,execution.Stages.Count);
    }

    [Theory]
    [InlineData("adjacent",true)] [InlineData("same-generation",false)] [InlineData("intervening-command",false)]
    [InlineData("foreign-receipt",false)] [InlineData("unfinished-validation",false)] [InlineData("publication-before-validation",false)]
    [InlineData("different-source-archive",false)]
    [InlineData("missing-provision-template",false)] [InlineData("mismatched-provision-template",false)]
    [InlineData("node-adjacent",true)] [InlineData("node-no-restore",false)] [InlineData("node-wrong-lock",false)]
    [InlineData("node-unfinished-restore",false)] [InlineData("node-wrong-script",false)]
    [InlineData("node-network",true)] [InlineData("node-network-no-restore",false)]
    [InlineData("node-job",true)] [InlineData("node-job-running",false)] [InlineData("node-job-wrong-key",false)]
    public async Task RealReadinessValidationRequiresAdjacentExactCandidateCommandAndPublicationReceipts(string condition,bool valid)
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var node=condition.StartsWith("node-",StringComparison.Ordinal);
        var jobRestore=condition.StartsWith("node-job",StringComparison.Ordinal);
        var networked=condition.StartsWith("node-network",StringComparison.Ordinal)||jobRestore;
        using(var buffer=new MemoryStream()) {using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true)) {
            using(var writer=new StreamWriter(zip.CreateEntry("index.html").Open()))writer.Write("<h1>Exact source</h1>");
            if(node) {
                using(var writer=new StreamWriter(zip.CreateEntry("package.json").Open()))writer.Write("""{"scripts":{"test":"node test.js","build":"node build.js"}}""");
                using(var writer=new StreamWriter(zip.CreateEntry("package-lock.json").Open()))writer.Write("""{"lockfileVersion":3,"packages":{"":{}}}""");
            }
        }f.Host.SnapshotArchive=buffer.ToArray();}
        var ready=await ReachReadiness(f);
        var plan=await f.Db.WorkDeliveryPlans.Include(p=>p.Executions).ThenInclude(e=>e.Stages).SingleAsync();
        var execution=plan.Executions.Single(e=>e.Id==ready.Id);var stage=execution.Stages.Single(s=>s.Id==ready.Stages.Last().Id);
        var installation=Guid.NewGuid();f.Technical.AgentInstallationId=installation;stage.AgentInstallationId=installation;
        f.Db.ScopedActionGrants.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.AgentInstallation,
            SubjectId=installation,ScopeKind=GrantScopeKind.Board,ScopeId=f.Board.Id,Action=CSweet.Contracts.WorkManagement.WorkItemActions.Read});
        var candidate=ready.Candidate!;var repository=candidate.Repositories.Single();var archiveHash=Convert.ToHexStringLower(SHA256.HashData(f.Host.SnapshotArchive));
        var now=DateTimeOffset.UtcNow;var environment=new EnvironmentRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,WorkstreamId=f.Project,
            InstallationId=installation,ProviderId="test-provider",ProviderNodeId=Guid.NewGuid(),Generation=4,State=CSweet.Domain.Compute.ComputeLifecycleState.Ready,LeaseExpiresAt=now.AddHours(1)};
        // Match the broker's persisted string enums, including persistence and network mode.
        environment.SpecificationJson=JsonSerializer.Serialize(new CSweet.Domain.Compute.ComputeSpecification("linux","x64","test",new(2,2048,20480),3600,
            Network:networked?new(CSweet.Domain.Compute.ComputeNetworkMode.OutboundOnly,true):null),ComputeProtocol.Json);
        if(condition!="missing-provision-template")f.Db.Add(new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,
            EnvironmentId=environment.Id,Generation=1,Action=InfrastructureActions.Provision,Status="Completed",CompletedAt=now.AddSeconds(-6),
            TemplateJson=condition=="mismatched-provision-template"?"{\"id\":\"other\",\"features\":[]}":"{\"id\":\"test\",\"features\":[\"docker-apps-v1\"]}"});
        var requestId=Guid.NewGuid();var root=$"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
        var command=new ComputeGuestCommand(requestId,"/bin/sh","/var/lib/csweet-compute/work",["-c",DeliveryComputeTest.ValidationScript(root,repository,archiveHash,node?"node":"static",plan.Id)],30,8192);
        var validation=new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,
            Generation=3,Action=InfrastructureActions.Execute,Status="Completed",CreatedAt=now.AddSeconds(-3),CompletedAt=now.AddSeconds(-2),
            WorkloadJson=Encode(new ComputeWorkload(command)),
            ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),requestId,0,false,Encoding.UTF8.GetBytes(DeliveryComputeTest.Marker(repository.CandidateCommitSha,archiveHash)),[],false)))};
        var publication=new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,
            Generation=4,Action=InfrastructureActions.PublishPort,Status="Completed",CreatedAt=now.AddSeconds(-1),CompletedAt=now,
            WorkloadJson=Encode(new ComputeWorkload(PublishPort:8080)),ResultJson=Encode(new ComputeWorkloadResult(Url:"http://127.0.0.1:1234/",UrlExpiresAt:now.AddHours(1)))};
        switch(condition) {case "same-generation":validation.Generation=4;break;case "intervening-command":validation.Generation=2;break;
            case "foreign-receipt":validation.InstallationId=Guid.NewGuid();break;case "unfinished-validation":validation.Status="Pending";break;
            case "publication-before-validation":publication.CreatedAt=now.AddSeconds(-4);break;}
        f.Db.AddRange(environment,validation,publication);await f.Db.SaveChangesAsync();
        if(node && condition is not ("node-no-restore" or "node-network-no-restore")) {
            var dependencies=DeliveryNodeDependencies.Read(f.Host.SnapshotArchive);
            var script=DeliveryComputeTest.RestoreScript(root,archiveHash,dependencies.LockfileSha256,networked);
            var jobPrefix=$"compute-node2:{execution.Id:N}:{new string('a',64)}";var jobKey=DeliveryComputeTest.NetworkRestoreJobKey(jobPrefix);
            if(jobRestore)script=DeliveryComputeTest.ObserveNetworkRestoreScript(root,archiveHash,dependencies.LockfileSha256,
                condition=="node-job-wrong-key"?new string('b',64):jobKey);
            var install=new ComputeGuestCommand(Guid.NewGuid(),"/bin/sh","/var/lib/csweet-compute/work",["-c",condition=="node-wrong-script"?"echo pretend install":script],30,8192);
            var restore=new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,
                Generation=2,Action=InfrastructureActions.Execute,Status=condition=="node-unfinished-restore"?"Pending":"Completed",CreatedAt=now.AddSeconds(-5),CompletedAt=now.AddSeconds(-4),
                WorkloadJson=Encode(new ComputeWorkload(install,NodeDependencies:networked?null:new(condition=="node-wrong-lock"?new string('b',64):dependencies.LockfileSha256,[]))),
                IdempotencyKey=jobRestore?jobPrefix+":cmd:1":null,
                ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),install.RequestId,0,false,
                    jobRestore?Encoding.UTF8.GetBytes(DeliveryComputeTest.RestoreJobMarker(jobKey,condition=="node-job-running"?"running":"ok")):[],[],false)))};
            f.Db.Add(restore);await f.Db.SaveChangesAsync();
        }
        var proof=new WorkDeliveryComputeEvidence(repository.RepositoryId,repository.CandidateCommitSha,archiveHash,environment.Id,4,
            validation.Id,publication.Id,"http://127.0.0.1:1234/",now.AddHours(1));
        if(condition=="different-source-archive") f.Host.SnapshotArchive=[1,2,3];
        var review=new WorkDeliveryReviewResult(candidate.Digest,true,"Readiness only",[],[]){ComputeTests=[proof]};
        if(valid)await f.Service.ValidateReadinessAsync(plan,execution,stage,review,default);
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ValidateReadinessAsync(plan,execution,stage,review,default));
    }

    [Theory]
    [InlineData("exact",true)] [InlineData("unrelated-finding",false)] [InlineData("changed-generation",false)]
    [InlineData("failed-upload",false)] [InlineData("wrong-operation",false)] [InlineData("foreign-installation",false)]
    [InlineData("archive",true)] [InlineData("archive-with-unrelated-finding",false)]
    [InlineData("missing",true)] [InlineData("missing-unrelated-finding",false)]
    [InlineData("missing-changed-generation",false)] [InlineData("missing-changed-script",false)]
    [InlineData("missing-real-test-failure",false)]
    [InlineData("restore",true)] [InlineData("restore-diagnostic",true)]
    [InlineData("restore-unrelated-finding",false)] [InlineData("restore-changed-generation",false)]
    [InlineData("restore-real-failure",false)] [InlineData("restore-application-command",false)]
    [InlineData("restore-wrong-lock",false)] [InlineData("restore-wrong-diagnostic",false)]
    [InlineData("restore-foreign-installation",false)]
    [InlineData("restore-wrong-key",false)] [InlineData("restore-wrong-request",false)]
    [InlineData("restore-closed-evidence-grant",true)]
    [InlineData("restore-retired-finding",true)]
    [InlineData("truncated",true)] [InlineData("truncated-real-failure",false)]
    [InlineData("truncated-not-truncated",false)] [InlineData("truncated-changed-script",false)]
    [InlineData("truncated-no-restore",false)] [InlineData("truncated-unrelated-finding",false)]
    [InlineData("truncated-wrong-key",false)] [InlineData("truncated-changed-generation",false)]
    [InlineData("provision-cleaned",true)] [InlineData("provision-pending-cleanup",false)]
    [InlineData("provision-had-command",false)] [InlineData("provision-unrelated-finding",false)]
    [InlineData("provision-wrong-key",false)] [InlineData("provision-other-error",false)]
    public async Task OwnerCanSupersedeOnlyVerifiedLegacyCursorProcessingFailureAndMustRerunAllReleaseGates(string condition,bool valid)
    {
        await using var f=await HierarchicalDeliveryServiceTests.Fixture.Create(false);
        var managed=condition.StartsWith("restore",StringComparison.Ordinal);
        var truncated=condition.StartsWith("truncated",StringComparison.Ordinal);
        var provision=condition.StartsWith("provision",StringComparison.Ordinal);
        if(managed||truncated) { using var buffer=new MemoryStream();using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true)) {
            using(var writer=new StreamWriter(zip.CreateEntry("package.json").Open()))writer.Write("""{"scripts":{"build":"node build.js"}}""");
            using(var writer=new StreamWriter(zip.CreateEntry("package-lock.json").Open()))writer.Write("""{"lockfileVersion":3,"packages":{"":{}}}""");
        } f.Host.SnapshotArchive=buffer.ToArray(); }
        var ready=await ReachReadiness(f);
        var missing=condition.StartsWith("missing",StringComparison.Ordinal);
        var error=provision?WorkDeliveryService.FailedProvisionCursor:truncated?WorkDeliveryService.TruncatedComputeValidation:managed?WorkDeliveryService.InterruptedManagedRestore:missing?WorkDeliveryService.LegacyMissingDependencyFailure:condition.StartsWith("archive",StringComparison.Ordinal)?WorkDeliveryService.LegacySourceArchiveFailure:WorkDeliveryService.LegacyComputeCursorFailure;
        var rejection=f.Review(ready);rejection=rejection with {Result=rejection.Result with {Approved=false,Summary=error,
            Criteria=[new("Requirement works",false,error)],Findings=[error]}};
        await f.Service.CompleteReviewAsync(f.Org,f.Technical.Id,rejection);
        var plan=await f.Db.WorkDeliveryPlans.Include(p=>p.Findings).Include(p=>p.Executions).ThenInclude(e=>e.Stages).ThenInclude(s=>s.Attempts).SingleAsync();
        var execution=plan.Executions.Single(e=>e.Id==ready.Id);var current=execution.Stages.Single(s=>s.Id==ready.Stages.Last().Id);
        var installation=Guid.NewGuid();current.AgentInstallationId=installation;current.Traversal=1;f.Author.PermissionLevel=OrganizationPermissionLevel.Owner;
        var candidate=ready.Candidate!;var now=DateTimeOffset.UtcNow;var environment=new EnvironmentRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,
            WorkstreamId=f.Project,InstallationId=installation,ProviderId="test-provider",ProviderNodeId=Guid.NewGuid(),Generation=2,State=CSweet.Domain.Compute.ComputeLifecycleState.Ready,LeaseExpiresAt=now.AddHours(1)};
        var command=new ComputeGuestCommand(Guid.NewGuid(),"/bin/sh","/var/lib/csweet-compute/work",["-c","upload exact source"],30,8192);
        var operation=new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,Generation=2,
            Action=InfrastructureActions.Execute,Status="Completed",CompletedAt=now,IdempotencyKey=$"delivery-compute:{execution.Id:N}:{candidate.Digest}:cmd:0",
            WorkloadJson=Encode(new ComputeWorkload(command)),ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),command.RequestId,0,false,[],[],false)))};
        if(missing) {
            var repository=candidate.Repositories.Single();var root=$"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
            var script=DeliveryComputeTest.ValidationScript(root,repository,new string('a',64),"docker",plan.Id)
                .Replace("if [ 'docker' != node ] && [ -f source/package.json ]; then","if [ -f source/package.json ]; then",StringComparison.Ordinal);
            command=command with {Arguments=["-c",condition=="missing-changed-script"?script+"\necho changed":script]};
            operation.WorkloadJson=Encode(new ComputeWorkload(command));
            operation.Generation=environment.Generation=11;
            var stderr=condition=="missing-real-test-failure"?"Actual game assertion failed":error["Compute validation failed or its result is unknown: ".Length..];
            operation.ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),command.RequestId,1,false,[],Encoding.UTF8.GetBytes(stderr),false)));
        }
        if(managed) {
            var repository=candidate.Repositories.Single();var root=$"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
            var dependencies=DeliveryNodeDependencies.Read(f.Host.SnapshotArchive);
            var archiveHash=Convert.ToHexStringLower(SHA256.HashData(f.Host.SnapshotArchive));
            var epoch=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Digest+":initial")));
            var key=$"compute-node2:{execution.Id:N}:{epoch}";
            operation.IdempotencyKey=condition=="restore-wrong-key"?"unrelated-candidate":key+":cmd:0";
            command=command with {RequestId=condition=="restore-wrong-request"?Guid.NewGuid():new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key+":0")).AsSpan(0,16)),
                Arguments=["-c",condition=="restore-application-command"?"npm test":DeliveryComputeTest.RestoreScript(root,archiveHash,dependencies.LockfileSha256)]};
            var ingress=new ComputeNodeDependencies(condition=="restore-wrong-lock"?new string('b',64):dependencies.LockfileSha256,
                dependencies.Packages.Select(p=>new ComputeNodePackage(p.Url,p.Integrity)).ToArray());
            operation.WorkloadJson=Encode(new ComputeWorkload(command,NodeDependencies:ingress));
            operation.ResultJson=condition=="restore-real-failure"?Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),command.RequestId,1,false,[],Encoding.UTF8.GetBytes("Real npm failure"),false))):Encode(new ComputeWorkloadResult(ErrorCode:"outcome-unknown"));
            if(condition is "restore-diagnostic" or "restore-wrong-diagnostic") {
                environment.Generation++;
                var inspection=new ComputeGuestCommand(Guid.NewGuid(),"/bin/sh","/var/lib/csweet-compute/work",["-c",condition=="restore-wrong-diagnostic"?"echo unrelated":WorkDeliveryService.ManagedRestoreDiagnosticScript],30,8192);
                f.Db.Add(new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,Generation=environment.Generation,
                    Action=InfrastructureActions.Execute,Status="Completed",CompletedAt=now,WorkloadJson=Encode(new ComputeWorkload(inspection)),
                    ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),inspection.RequestId,0,false,[],[],false)))});
            }
        }
        if(truncated) {
            var repository=candidate.Repositories.Single();var root=$"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
            var dependencies=DeliveryNodeDependencies.Read(f.Host.SnapshotArchive);var archive=Convert.ToHexStringLower(SHA256.HashData(f.Host.SnapshotArchive));
            var epoch=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Digest+":initial")));
            var key=$"compute-node2:{execution.Id:N}:{epoch}";
            operation.IdempotencyKey=condition=="truncated-wrong-key"?"unrelated-candidate":key+":cmd:1";
            var script=WorkDeliveryService.LegacyVerboseNodeValidationScript(root,repository,archive,plan.Id);
            Assert.DoesNotContain("> validation.log",script);
            command=command with {RequestId=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key+":1")).AsSpan(0,16)),Arguments=["-c",condition=="truncated-changed-script"?script+"\necho changed":script]};
            operation.WorkloadJson=Encode(new ComputeWorkload(command));operation.CreatedAt=now.AddSeconds(-1);
            operation.ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),command.RequestId,
                condition=="truncated-real-failure"?1:0,false,Encoding.UTF8.GetBytes("Verbose log prefix"),[],condition!="truncated-not-truncated")));
            if(condition!="truncated-no-restore") {
                var install=new ComputeGuestCommand(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key+":0")).AsSpan(0,16)),"/bin/sh",command.WorkingDirectory,
                    ["-c",DeliveryComputeTest.RestoreScript(root,archive,dependencies.LockfileSha256)],30,8192);
                f.Db.Add(new OperationRecord {Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,Generation=1,
                    Action=InfrastructureActions.Execute,Status="Completed",IdempotencyKey=key+":cmd:0",CreatedAt=now.AddSeconds(-4),CompletedAt=now.AddSeconds(-2),
                    WorkloadJson=Encode(new ComputeWorkload(install,NodeDependencies:new(dependencies.LockfileSha256,[]))),
                    ResultJson=Encode(new ComputeWorkloadResult(new(1,Guid.NewGuid(),install.RequestId,0,false,[],[],false)))});
            }
        }
        if(provision) {
            current.CreatedAt=now.AddSeconds(1);environment.CreatedAt=now;
            environment.DesiredEnvironmentKey=condition=="provision-wrong-key"?"other-workspace":$"project-test-internet:{plan.WorkstreamId:N}";
            environment.State=CSweet.Domain.Compute.ComputeLifecycleState.Destroyed;environment.DesiredState=CSweet.Domain.Compute.ComputeDesiredState.Destroyed;
            environment.TeardownConfirmedAt=condition=="provision-pending-cleanup"?null:now;
            environment.SpecificationJson=JsonSerializer.Serialize(new CSweet.Domain.Compute.ComputeSpecification("linux","x64","test",new(2,2048,20480),3600,Network:new(CSweet.Domain.Compute.ComputeNetworkMode.OutboundOnly,true)),ComputeProtocol.Json);
            operation.Action=InfrastructureActions.Destroy;operation.WorkloadJson=null;operation.ResultJson=null;
            f.Db.Add(new OperationRecord{Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,
                Generation=1,Action=InfrastructureActions.Provision,Status="Superseded",CompletedAt=now,
                FailureCode=condition=="provision-other-error"?"different-failure":"provider-operation-failed"});
            if(condition=="provision-had-command")f.Db.Add(new OperationRecord{Id=Guid.NewGuid(),OrganizationId=f.Org,InstallationId=installation,EnvironmentId=environment.Id,
                Generation=1,Action=InfrastructureActions.Execute,Status="Completed",WorkloadJson=Encode(new ComputeWorkload(command))});
        }
        var prior=new WorkStageExecution {Id=Guid.NewGuid(),StageKey="build-readiness",Traversal=0,Status=WorkStageExecutionStatus.Completed,
            AgentInstallationId=installation,LastOutcomeCode="awaiting-compute"};
        if(managed||truncated||provision) prior.CreatedAt=current.CreatedAt.AddSeconds(-1);
        if(condition=="restore-closed-evidence-grant")foreach(var grant in await f.Db.ScopedActionGrants.Where(g=>g.SubjectId==f.Technical.Id&&g.Action==WorkDeliveryCapabilities.Evidence).ToListAsync())grant.RevokedAt=now;
        if(condition=="restore-retired-finding") { var retired=plan.Findings.Single();retired.Status="Superseded";retired.ResolutionEvidence="Fresh build readiness remains mandatory; prior processor failure retained."; }
        var attempt=new WorkExecutionAttempt {Id=Guid.NewGuid(),StageExecutionId=prior.Id,Status=WorkExecutionAttemptStatus.Completed,Attempt=1};
        attempt.ResultJson=Encode(new WorkExecutionOutcomeV1(prior.Id,attempt.Id,"Completed","awaiting-compute","Upload pending",
            JsonSerializer.SerializeToElement(new WorkDeliveryComputePending(candidate.Digest,provision?[]:[operation.Id],now.AddMinutes(5)),Json),[],[]));
        prior.Attempts.Add(attempt);execution.Stages.Add(prior);
        switch(condition) {case "unrelated-finding":case "archive-with-unrelated-finding":case "missing-unrelated-finding":case "restore-unrelated-finding":case "truncated-unrelated-finding":case "provision-unrelated-finding":plan.Findings.Single().Summary="Real missing game requirement";break;
            case "missing-changed-generation":case "restore-changed-generation":case "truncated-changed-generation":environment.Generation++;break;
            case "changed-generation":environment.Generation=3;break;case "failed-upload":operation.Status="Blocked";break;
            case "wrong-operation":operation.IdempotencyKey="another candidate";break;case "foreign-installation":case "restore-foreign-installation":operation.InstallationId=Guid.NewGuid();break;}
        f.Db.AddRange(environment,operation,prior);await f.Db.SaveChangesAsync();
        if(condition=="restore-closed-evidence-grant")await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>
            f.Service.ReadEvidenceAsync(f.Org,f.Technical.Id,new(plan.Id,execution.Id,candidate.Repositories.Single().RepositoryId)));
        var request=new RecoverWorkDeliveryRequest(plan.Id,execution.Id,execution.Revision,"verified-cursor-correction","The tested compute adapter now reconciles its own exact command receipts; rerun independent release validation.");
        if(!valid){await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.RecoverAsync(f.Org,f.Author.Id,request));Assert.All(plan.Findings,x=>Assert.Equal("Open",x.Status));return;}
        var recovered=await f.Service.RecoverAsync(f.Org,f.Author.Id,request);
        await f.Service.RecoverAsync(f.Org,f.Author.Id,request);
        Assert.Equal("WaitingForChildren",recovered.Executions.Single(e=>e.Id==execution.Id).Status);
        if(condition=="archive") {
            var fresh=Assert.Single(recovered.Executions,e=>e.Scope=="Release"&&e.Status!="Superseded");
            Assert.Equal(execution.Id,fresh.Id);Assert.Equal("WaitingForChildren",fresh.Status);
            Assert.Null(execution.BlockedReason);Assert.Equal(plan.ScopeRevision,recovered.ScopeRevision);
            Assert.All(recovered.Executions.Where(e=>e.Scope is "Story" or "Epic"),e=>Assert.Equal("Completed",e.Status));
        }
        Assert.All(plan.Findings,x=>{Assert.Equal("Superseded",x.Status);Assert.Contains("Fresh build readiness",x.ResolutionEvidence);});
        Assert.Equal("changes_requested",current.LastOutcomeCode);Assert.Null(execution.AcceptanceJson);
        Assert.Contains(recovered.Scopes.Single(s=>s.Scope=="Release").Stages,s=>s.StageKey=="quality");
        Assert.Contains(recovered.Scopes.Single(s=>s.Scope=="Release").Stages,s=>s.StageKey=="technical-review");
    }
}
