using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using W = CSweet.WorkManagement.Contracts;
using C = CSweet.Contracts.WorkManagement;
namespace CSweet.UnitTests;

/// <summary>Real PostgreSQL, board/artifact brokers, authorization, sprint readiness, review/rework and completion.
/// Approved inputs, workforce allocation and model/provider responses are controlled fixture boundaries.</summary>
public sealed class FreshPlanningPostgresJourneyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [LifecyclePostgresFact]
    public async Task FreshBoardPublishesThroughPublicProtocolStartsTwoPopulatedSprintsAndClosesOnlyAfterFinalVerification()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost" });
        settings.Database = "planning_journey_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync(); await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{settings.Database}\"",admin)) await create.ExecuteNonQueryAsync();
        try {
            await using var f = new HierarchicalDeliveryServiceTests.Fixture(true,new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options);
            await f.Db.Database.EnsureCreatedAsync(); await f.Db.Database.OpenConnectionAsync();
            await f.Db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await f.Seed();
            f.Db.WorkItemStageAssignments.RemoveRange(await f.Db.WorkItemStageAssignments.ToArrayAsync());
            f.Db.CoreWorkTasks.RemoveRange(await f.Db.CoreWorkTasks.ToArrayAsync()); await f.Db.SaveChangesAsync();
            Assert.Empty(await f.Db.CoreWorkTasks.ToArrayAsync());
            var publisher = new OrganizationUser { Id=Guid.NewGuid(),OrganizationId=f.Org,DisplayName="Independent publisher",EmployeeType=EmployeeType.Agent,
                AgentInstallationId=Guid.NewGuid(),ReportsToOrganizationUserId=f.Manager.Id,IsActive=true };
            f.Db.CoreOrganizationUsers.Add(publisher);
            f.Db.ProjectParticipants.Add(new() { OrganizationId=f.Org, WorkstreamId=f.Project, OrganizationUserId=publisher.Id, AddedByOrganizationUserId=f.Manager.Id });
            f.Db.TeamMemberships.Add(new() { Id=Guid.NewGuid(), OrganizationId=f.Org, TeamId=f.Board.TeamId!.Value, OrganizationUserId=publisher.Id });
            f.Db.AgentInstallations.Add(new() {Id=publisher.AgentInstallationId.Value,InstallationKey=Guid.NewGuid(),BusinessId=f.Org.ToString(),PackageVersionId=Guid.NewGuid()});
            f.Board.ManagerOrganizationUserId=publisher.Id; f.Board.ProfileKey=W.WorkBoardProfileKeys.ProjectDeliveryV2;
            foreach(var action in new[] {C.WorkBoardActions.Read,C.WorkBoardActions.Configure,C.WorkItemActions.Read,C.WorkItemActions.Create})
                f.Db.ScopedActionGrants.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.AgentInstallation,SubjectId=publisher.AgentInstallationId.Value,Action=action,ScopeKind=GrantScopeKind.Team,ScopeId=f.Board.TeamId});
            foreach(var action in W.WorkDeliveryCapabilities.All)
                f.Db.ScopedActionGrants.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.AgentInstallation,SubjectId=publisher.AgentInstallationId.Value,Action=action,ScopeKind=GrantScopeKind.Workstream,ScopeId=f.Project});
            int columnPosition=0;
            foreach(var (name,category) in new[]{("Ready",WorkBoardColumnCategory.ToDo),("Working",WorkBoardColumnCategory.InProgress),("Done",WorkBoardColumnCategory.Done)})
                f.Db.WorkBoardColumns.Add(new(){Id=Guid.NewGuid(),BoardId=f.Board.Id,Name=name,Category=category,Position=columnPosition++});
            var source=new W.WorkApprovedSource(Guid.NewGuid(),Guid.NewGuid(),new string('a',64));
            f.Db.CoreArtifacts.Add(new(){Id=source.DocumentId,OrganizationId=f.Org,WorkstreamId=f.Project,Title="Approved arcade outcomes",AcceptedRevisionId=source.RevisionId,LatestRevisionId=source.RevisionId});
            f.Db.ArtifactRevisions.Add(new(){Id=source.RevisionId,OrganizationId=f.Org,ArtifactId=source.DocumentId,Status=ArtifactRevisionStatus.Accepted,ContentSha256=source.Digest,Content="Deliver the technical proof and then the remaining approved outcome. Requirement works."});
            f.Db.ScopedActionGrants.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Org,SubjectKind=GrantSubjectKind.AgentInstallation,SubjectId=publisher.AgentInstallationId.Value,
                ScopeKind=GrantScopeKind.Artifact,ScopeId=source.DocumentId,Action=CSweet.Contracts.Core.ArtifactActions.Read});
            await f.Db.SaveChangesAsync();
            var auth=new ScopedActionAuthorizationService(f.Db);var audit=new TestAuditEventWriter();
            var work=new WorkManagementCapabilityHandler(f.Db,auth,audit,new WorkOrchestrationService(f.Db,TimeProvider.System,auth));
            var documents=new ArtifactCapabilityHandler(f.Db,new ArtifactDocumentService(f.Db,audit,TimeProvider.System),audit,TimeProvider.System);
            var session=new AgentSession("public-participant","example.external-producer",publisher.AgentInstallationId.ToString()!,f.Org.ToString(),Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),
                new(new HashSet<string>(),new HashSet<string>(),new HashSet<string>{WorkItemCapabilities.Read,WorkItemCapabilities.Create,PlatformCapabilities.ArtifactRead},1));
            async Task<T> Invoke<T>(string capability,object input) {
                var handler=(IPlatformCapabilityHandler)(documents.CanHandle(capability)?documents:work);
                var request=new RequestCapability {RequestId=Guid.NewGuid().ToString(),Capability=capability,Payload=JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(input,Json))};
                var replies=new List<CapabilityResult>();await foreach(var r in handler.HandleAsync(session,request,default))replies.Add(r);
                var reply=Assert.Single(replies);Assert.True(reply.Succeeded,reply.Error);return JsonSerializer.Deserialize<T>(reply.Payload.Span,Json)!;
            }
            var states=new Dictionary<string,AgentOperatingStateResponse>();
            var runtime=new AgentTestRuntime()
                .RegisterCapability<W.WorkBoardReference,W.WorkBoardDetail>(WorkItemCapabilities.Read,(r,_)=>Invoke<W.WorkBoardDetail>(WorkItemCapabilities.Read,r))
                .RegisterCapability<W.CreateWorkItemRequest,W.WorkItem>(WorkItemCapabilities.Create,(r,_)=>Invoke<W.WorkItem>(WorkItemCapabilities.Create,r))
                .RegisterCapability<object,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(r,_)=>Invoke<ArtifactDocument>(PlatformCapabilities.ArtifactRead,r))
                .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=>Task.FromResult(states[r.StateKey]=new(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)))
                .RegisterCapability<W.ConfigureWorkDeliveryPlanRequest,W.WorkDeliveryPlanResponse>(W.WorkDeliveryCapabilities.Configure,(r,ct)=>f.Service.ConfigureAsync(f.Org,publisher.Id,r,ct))
                .RegisterCapability<W.ControlWorkDeliveryPlanRequest,W.WorkDeliveryPlanResponse>(W.WorkDeliveryCapabilities.Control,(r,ct)=>f.Service.ControlAsync(f.Org,publisher.Id,r,ct))
                .RegisterCapability<W.ReadWorkDeliveryPlansRequest,IReadOnlyList<W.WorkDeliveryPlanResponse>>(W.WorkDeliveryCapabilities.Read,(r,ct)=>f.Service.ReadAsync(f.Org,publisher.Id,r,ct));
            var context=runtime.CreateContext(f.Org.ToString(),publisher.AgentInstallationId.ToString()!,new(publisher.Id.ToString(),"Publisher",null,null,null,[],null,f.Manager.Id.ToString(),"Director"));
            var releases=new List<Guid>();var originalIds=new List<Guid>();
            for(int sequence=1;sequence<=2;sequence++) {
                if(sequence>1)f.BeginNextDeclaredIncrement();
                var input=new WorkRefinementRequest(f.Project,f.Board.Id,"Approaching approved outcome "+sequence,new(source.DocumentId,source.RevisionId,source.Digest),
                    [new("Epic",W.WorkItemTypeKeys.ProjectEpicV2,false,null),new("Story",W.WorkItemTypeKeys.ProjectStoryV2,false,"Epic"),new("Task",W.WorkItemTypeKeys.ProjectTaskV2,true,"Story")],
                    [new("game-engineer","Artifact","specialist-execution")],[]);
                var draft=new WorkRefinementDraft("Source-bound approaching work "+sequence,[
                    new("Epic","Outcome "+sequence,"Approved deliverable",["Requirement works"],null,[],null),
                    new("Story","Slice "+sequence,"Independently verifiable outcome",["Requirement works"],0,[],null),
                    new("Task","Implementation "+sequence,"Deliver requirement with exact document evidence",["Requirement works"],1,[],"game-engineer")]);
                var ids=await ScopedWorkRefinement.PublishAsync(input,draft,"reviewed-scope-"+sequence,context,default);
                Assert.Equal(ids,await ScopedWorkRefinement.PublishAsync(input,draft,"reviewed-scope-"+sequence,context,default));
                f.Epic=await f.Db.CoreWorkTasks.SingleAsync(i=>i.Id==ids[0]);f.Story=await f.Db.CoreWorkTasks.SingleAsync(i=>i.Id==ids[1]);f.Task=await f.Db.CoreWorkTasks.SingleAsync(i=>i.Id==ids[2]);
                foreach(var (stage,person) in new[]{("development",f.Author),("quality",f.Qa),("technical-review",f.Technical)})
                    f.Db.WorkItemStageAssignments.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Org,BoardId=f.Board.Id,WorkItemId=f.Task.Id,StageKey=stage,PrincipalKind=WorkOrchestrationPrincipalKind.Human,OrganizationUserId=person.Id});
                f.Task.AccountableOrganizationUserId=f.Author.Id;await f.Db.SaveChangesAsync();
                await HierarchicalTaskRuntimeTests.RunJourney(f,true,game:true,publicSprintSequence:sequence);
                for(int pulse=0;pulse<12;pulse++) {await f.Service.PulseAsync();var plan=await f.Read();if(plan.Status=="Completed")break;
                    var review=plan.Executions.FirstOrDefault(e=>e.Status=="WaitingForHuman");if(review is not null){await f.Approve(review);continue;}
                    var acceptance=plan.Executions.FirstOrDefault(e=>e.Status=="WaitingForApproval");if(acceptance is not null)await f.Accept(acceptance);}
                var increment=await f.Read();Assert.Equal("Completed",increment.Status);releases.Add(increment.Scopes.Single(s=>s.Scope=="Release").TicketWorkflow!.TicketId);
                if(sequence==1)originalIds.AddRange(ids);else foreach(var id in originalIds)Assert.Equal(WorkTaskStatus.Completed,(await f.Db.CoreWorkTasks.SingleAsync(i=>i.Id==id)).Status);
            }
            Assert.Equal(2,await f.Db.WorkSprints.CountAsync());Assert.All(await f.Db.WorkSprints.ToArrayAsync(),s=>Assert.Equal(WorkSprintStatus.Completed,s.Status));
            var specification=new FinalProductSpecification(f.Project,f.Board.Id,publisher.Id,source.Digest,new(["Deliver requirement"],["Requirement works"]),releases,[],
                [new("assess-product","review",new("assess-product","Human",f.Technical.Id))]) {CompletionSource=source};
            var final=await FinalProductDelivery.PrepareAsync(specification,context,default);f.PlanId=final.Id;
            var project=await f.Db.Workstreams.SingleAsync(p=>p.Id==f.Project);var proof=new W.WorkstreamDeliveryCompletion(final.Id,final.ScopeRevision,source);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkstreamDeliveryCompletionValidator.ValidateAsync(f.Db,project,proof,default));
            await f.Service.PulseAsync();await f.Approve((await f.Read()).Executions.Single());await f.Service.PulseAsync();
            Assert.Equal("Completed",(await f.Read()).Status);
            var completion=new WorkstreamDeliveryCompletionService(f.Db,TimeProvider.System);
            var command=new W.CompleteWorkstreamDeliveryRequest(f.Project,project.Revision,proof,"verified-close");
            Assert.True((await completion.CompleteAsync(f.Org,f.Manager.Id,command,default)).Applied);
            Assert.True((await completion.CompleteAsync(f.Org,f.Manager.Id,command,default)).Applied);
            Assert.Equal(2,await f.Db.WorkSprints.CountAsync()); // final acceptance creates no empty successor
        } finally {Assert.StartsWith("planning_journey_",settings.Database);await using var drop=new NpgsqlCommand($"DROP DATABASE \"{settings.Database}\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync();}
    }
    private sealed class LifecyclePostgresFactAttribute:FactAttribute
    {
        public LifecyclePostgresFactAttribute(){if(Environment.GetEnvironmentVariable("CSWEET_REQUIRE_LIFECYCLE_DATABASE")!="1"&&string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))Skip="Run the required lifecycle script with isolated PostgreSQL.";}
    }
}
