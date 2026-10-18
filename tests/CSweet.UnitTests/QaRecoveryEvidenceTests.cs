using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class QaRecoveryEvidenceTests
{
    [Theory]
    [InlineData("current",true)] [InlineData("amended-planning",true)]
    [InlineData("legacy-origin-scope",true)] [InlineData("wrong-project",false)]
    [InlineData("another-source",false)] [InlineData("agent-authored",false)]
    [InlineData("audit-mismatch",false)] [InlineData("tampered-content",false)]
    [InlineData("erased-source",false)]
    [InlineData("retained-with-new-draft",true)]
    public async Task RetainedObservationsRequireScopedHumanAndExactSourceAuditWithoutGrantingAVerdict(string change,bool expected)
    {
        var databaseName=Guid.NewGuid().ToString();
        await using var f=new HierarchicalDeliveryServiceTests.Fixture(false,
            new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(databaseName).Options);
        await f.Seed();
        var install=Guid.NewGuid();f.Db.AgentInstallations.Add(new() {Id=install,BusinessId=f.Org.ToString(),IsEnabled=true,
            RevisionStatus=PluginRevisionStatus.Active,SetupState=PluginSetupState.Ready});
        var execution=new WorkItemExecution {Id=Guid.NewGuid(),WorkItemId=f.Task.Id};
        var stage=new WorkStageExecution {Id=Guid.NewGuid(),ItemExecutionId=execution.Id,StageKey="quality"};
        var attempt=new WorkExecutionAttempt {Id=Guid.NewGuid(),StageExecutionId=stage.Id,Status=WorkExecutionAttemptStatus.Completed};
        f.Db.AddRange(execution,stage,attempt);await f.Db.SaveChangesAsync();
        var sha=new string('a',40);
        var original=JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {Scope="Task",ItemId=f.Task.Id,OrganizationId=f.Org,
            StageExecutionId=stage.Id,AttemptId=attempt.Id,BoardId=f.Board.Id,WorkstreamId=f.Project,
            SprintId=Guid.NewGuid(),SprintExecutionId=Guid.NewGuid(),PolicyRevisionId=Guid.NewGuid(),PlanningRevision=1,AssignmentRevision=1,
            Item=JsonSerializer.SerializeToElement(new {}),Input=JsonSerializer.SerializeToElement(new WorkExecutionInputV1(f.Project,f.Board.TeamId,1,new([],[]))),PriorOutcomes=[],Evidence=[],
            Candidate=new("original",1,[new(f.Repositories[0],"story","story",sha,sha,sha)],[])};
        var inbox=new AgentWorkInbox(f.Db,new EphemeralDataProtectionProvider(),TimeProvider.System);
        var work=await inbox.EnqueueAsync(f.Org.ToString(),install,AgentWorkKind.Capability,"work.execution.run.v1",
            JsonSerializer.SerializeToElement(original,WorkDeliveryService.Json),"old-qa-observations",DateTimeOffset.UtcNow.AddHours(1));
        work.Status=AgentWorkStatus.Completed;attempt.AgentWorkItemId=work.Id;
        var run=new AgentRunLog {Id=Guid.NewGuid(),AgentWorkItemId=work.Id,AgentInstallationId=install,Status="Completed"};f.Db.Add(run);
        var audit=new AuditEvent {Id=Guid.NewGuid(),OrganizationId=f.Org,EventType="model.call.completed",EntityType="AgentRunLog",EntityId=run.Id,EvidenceSha256=new string('c',64)};f.Db.Add(audit);
        var retained=new WorkOrchestrator.QaRecoveryEvidence(f.Task.Id,stage.Id,attempt.Id,1,1,sha,audit.Id,
            change=="audit-mismatch"?new string('d',64):audit.EvidenceSha256,JsonSerializer.SerializeToElement(new {command="npm test",observed="12 passed; no browser result"}));
        var content=WorkDeliveryService.Encode(retained);var revisionId=Guid.NewGuid();
        var doc=new Artifact {Id=Guid.NewGuid(),OrganizationId=f.Org,OriginWorkItemId=f.Task.Id,WorkstreamId=f.Board.WorkstreamId,TeamId=f.Board.TeamId,
            CreatedByOrganizationUserId=change=="agent-authored"?f.Author.Id:f.Manager.Id,DocumentType=WorkOrchestrator.QaRecoveryDocumentType,SubmittedRevisionId=revisionId};
        if(change=="wrong-project")doc.WorkstreamId=Guid.NewGuid();
        var revision=new ArtifactRevision {Id=revisionId,OrganizationId=f.Org,ArtifactId=doc.Id,Content=content,CreatedByOrganizationUserId=doc.CreatedByOrganizationUserId,
            ContentSha256=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))};
        if(change is "legacy-origin-scope" or "retained-with-new-draft") {
            f.Manager.ApplicationUserId=Guid.NewGuid();f.Manager.PermissionLevel=OrganizationPermissionLevel.Owner;
            await f.Db.SaveChangesAsync();
            var service=new ArtifactDocumentService(f.Db,new TestAuditEventWriter(),TimeProvider.System);
            var actor=new ArtifactHumanActor(f.Manager.ApplicationUserId.Value);
            var created=await service.CreateAsync(f.Org,actor,new("Retained observations",content,WorkOrchestrator.QaRecoveryDocumentType,
                "retain-observations",OriginWorkItemId:f.Task.Id) {WorkstreamId=f.Project,TeamId=f.Board.TeamId});
            await service.SubmitAsync(f.Org,actor,created.Document.Id,new(created.LatestRevision.Id,"retain-submit",ReviewerOrganizationUserId:f.Manager.Id));
            if (change == "retained-with-new-draft")
                await service.ReviseAsync(f.Org, actor, created.Document.Id,
                    new(created.LatestRevision.Id, "unsubmitted draft must never replace the historical input", "new-draft"));
        } else {f.Db.AddRange(doc,revision);await f.Db.SaveChangesAsync();}
        if(change=="tampered-content"){revision.Content+=" ";await f.Db.SaveChangesAsync();}
        if(change=="erased-source") {
            // Model the server erasure transaction, which alone can create this persisted tombstone.
            await using var erasure=new DbContext(new DbContextOptionsBuilder().UseInMemoryDatabase(databaseName).UseModel(f.Db.Model).Options);
            var erased=await erasure.Set<AgentWorkItem>().SingleAsync(x=>x.Id==work.Id);
            erased.MemoryErasedAt=DateTimeOffset.UtcNow;await erasure.SaveChangesAsync();
        }
        var current=original with {OrganizationId=f.Org,WorkstreamId=f.Project,BoardId=f.Board.Id,StageKey="quality",AttemptId=Guid.NewGuid(),
            PlanningRevision=change=="amended-planning"?2:1,
            Candidate=change=="another-source"?new("new",1,[new(f.Repositories[0],"story","story",new string('b',40),new string('b',40),new string('b',40))],[]):original.Candidate};
        var orchestrator=new WorkOrchestrator(f.Db,inbox,null!,null!,[],TimeProvider.System,NullLogger<WorkOrchestrator>.Instance);
        if(change is "audit-mismatch" or "tampered-content" or "erased-source") {
            var warning=Assert.Single(await orchestrator.QaRecoveryEvidenceAsync(current,f.Board,default));
            Assert.Equal("qa-recovery-warning.v1",warning.Kind);Assert.DoesNotContain("12 passed",warning.Value);
        }
        else {
            var evidence=await orchestrator.QaRecoveryEvidenceAsync(current,f.Board,default);
            if(expected){var entry=Assert.Single(evidence);Assert.Equal("qa-recovered-evidence.v1",entry.Kind);Assert.Contains("no browser result",entry.Value);}
            else Assert.Empty(evidence);
        }
        Assert.Null(attempt.ResultJson); // Historical input never fabricates a report or advances the task.
    }
}
