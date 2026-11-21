using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.TrustedServices;
using CSweet.UnitTests;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Fixture = CSweet.UnitTests.HierarchicalDeliveryServiceTests.Fixture;
using TechnicalAgent = CSweet.Agent.TechnicalDirector.VideoGame.SpecialistAgent;
using QaAgent = CSweet.Agent.QA.VideoGame.SpecialistAgent;

namespace CSweet.DeliveryWorkflow.Tests;

/// <summary>Real Git, production agent callbacks and orchestration; scripted model/broker transport.
/// Human completion is the dispatch boundary, so this does not claim live grants or provider certification.</summary>
public sealed partial class AgentDeliveryJourneyTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static JsonElement Element(object value)=>JsonSerializer.SerializeToElement(value,Json);
    private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewCorrectionRealIntegrationFailedQaReviewedReworkAndPassedQaReachDone(bool playableDemo)
    {
        await using var f=await Fixture.Create(false);
        var root=Path.Combine(Path.GetTempPath(),"csweet-agent-journey",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,".csweet-git-store"),"journey");
        var store=new InternalGitRepositoryStore(Options.Create(new InternalGitStorageOptions {
            RepositoryRoot=root,ExpectedStoreId="journey",TemporaryRoot=Path.Combine(root,"operations")}));
        var artifacts=new WorkspaceArtifactValidator(); var localQaPaths=new List<string>();
        var previous = SetWorkspaceLimits();
        try
        {
            await store.ExecuteAsync(new(f.Org,f.Repositories[0],"create","main"));
            f.Client=DispatchProxy.Create<ITrustedSourceControlHostClient,Host>(); ((Host)f.Client).Store=store;
            var package=Guid.NewGuid(); var members=new[]{new ArtifactPackageMemberDigest(Guid.NewGuid(),Guid.NewGuid(),"brief",Hash("brief"))};
            var planning=JsonSerializer.Deserialize<WorkItemPlanningSpecification>(f.Task.PlanningSpecificationJson!,Json)! with {
                ArtifactPackageDigest=new(package,1,ArtifactPackageDigestCalculator.Calculate(package,1,members),DateTimeOffset.UtcNow,members)};
            f.Task.PlanningSpecificationJson=JsonSerializer.Serialize(planning,Json); f.Task.AssignmentRevision=1; await f.Db.SaveChangesAsync();
            var cycle=0; var reviewedPages=0; string? malformedPage=null; var correctedPage=false; var qaRuns=0;
            async Task<DeliveryBranchResult> Publish(Fixture current,string branch)
            {
                var workspace=Guid.NewGuid(); var source=await store.PrepareAsync(new(f.Org,f.Repositories[0],workspace,"csweet/story/one",branch,null,"prepare"),artifacts);
                var path=Path.Combine(root,"author-"+cycle); await artifacts.ExtractZipAsync(new MemoryStream(source.Archive),path);
                await File.WriteAllTextAsync(Path.Combine(path,"state.json"),JsonSerializer.Serialize(new {works=cycle++>0}));
                // Authored fixture, never a fetched project's script executed on the host.
                await File.WriteAllTextAsync(Path.Combine(path,"verify.cjs"),"const s=require('./state.json'); console.log('works='+s.works); process.exit(s.works?0:1);");
                if (playableDemo)
                {
                    var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StarCollector");
                    foreach (var name in new[] { "index.html", "game.js", "verify.cjs" })
                    {
                        var content = await File.ReadAllTextAsync(Path.Combine(fixture, name));
                        // First candidate has a real movement defect. Reviewed rework repairs the shipped source.
                        if (name == "game.js" && cycle == 1) content = content.Replace("SPEED = 5", "SPEED = 0");
                        await File.WriteAllTextAsync(Path.Combine(path, name), content);
                    }
                }
                // Span at least fifteen model review sections, independent of transport page boundaries.
                await File.WriteAllTextAsync(Path.Combine(path,"design.txt"),new string('x',360001));
                using var archive=new MemoryStream(); var manifest=await artifacts.CreateZipAsync(path,archive);
                await store.ApplySnapshotAsync(new(f.Org,f.Repositories[0],workspace,"publish",source.BaseCommitSha,branch,"csweet/story/one",
                    "publish-"+cycle,archive.ToArray(),manifest.Sha256,manifest.FileCount,manifest.TotalBytes,"Fixture implementation"),artifacts);
                return await f.Client.DeliveryBranchAsync(new(f.Org,f.Repositories[0],"InternalGit",null,"","","inspect",branch,"csweet/story/one","inspect"));
            }
            async Task<WorkExecutionOutcomeV1> Review(Fixture current,SourceControlPublication publication,DeliveryBranchResult refs)
            {
                var inspection=await store.ExecuteAsync(new(f.Org,f.Repositories[0],"compare",Name:publication.TargetBranch,ExpectedSha:publication.CommitSha));
                var patch=inspection.Content!; Assert.NotEmpty(patch);
                var reference=new GitMergeReview(publication.Id,publication.RepositoryId,f.Task.Id,"Fixture",publication.CommitSha,null,"",[],[],"AwaitingValidation") {
                    SourceBranch=publication.TicketBranch,TargetBranch=publication.TargetBranch,TargetCommitSha=refs.TargetCommitSha,
                    PatchDigest=Hash(patch),ChangedFiles=inspection.Files};
                var runtime=StateRuntime().RegisterCapability<WorkItemReference,WorkItem>(WorkManagementCapabilityNames.ItemRead,(_,_)=>Task.FromResult(Current("technical-review")))
                    .RegisterCapability<ReviewGitMergeRequest,GitMergeReview>(GitMergeCapabilities.Review,(r,_)=>{
                        DeliveryWorkflowBridge.ValidateReview(r); Assert.False(r.IncludePatch);
                        if(r.DiffRead is not {} read) return Task.FromResult(reference);
                        var text=patch.Substring(read.Offset,Math.Min(12000,patch.Length-read.Offset));
                        return Task.FromResult(reference with {DiffPage=new(reference.PatchDigest!,read.Offset,patch.Length,text,
                            read.Offset+text.Length<patch.Length?read.Offset+text.Length:null)});
                    }).RegisterCapability<JsonElement,JsonElement>(PlatformCapabilities.LlmChatStream,(r,_)=>{
                        Assert.Equal(JsonValueKind.Object,r.GetProperty("responseSchema").ValueKind);
                        var user=r.GetProperty("messages").EnumerateArray().First(m=>m.GetProperty("role").GetString()=="user");
                        var input=JsonDocument.Parse(user.GetProperty("text").GetString()!).RootElement;
                        object response;
                        if(input.TryGetProperty("page",out var page))
                        {
                            reviewedPages++;
                            if(reviewedPages==4)
                            {
                                malformedPage=page.GetRawText();
                                return Task.FromResult(Element(new {text="{\"summary\":\"bad fourth page\"]}}",finishReason="stop"}));
                            }
                            if(reviewedPages==5) correctedPage=page.GetRawText()==malformedPage;
                            response=new {summary="Inspected authored source; runtime checks follow independently.",blockingFindings=Array.Empty<object>(),observations=Array.Empty<string>(),questions=Array.Empty<string>()};
                        }
                        else response=new {summary="Complete source review; integration and independent checks must follow.",additionalFindings=Array.Empty<object>(),
                            resolutions=Array.Empty<object>(),criteria=new[]{new {satisfied=true,evidence="Implementation and test are present in the reviewed source.",evidenceOffsets=new[]{0}}}};
                        return Task.FromResult(Element(new {text=JsonSerializer.Serialize(response,Json),finishReason="stop"}));
                    });
                return await Execute(runtime,new TechnicalAgent(),Assignment("technical-review","game-technical-director"));
            }
            async Task<WorkExecutionOutcomeV1> Quality(Fixture current,string commit,bool expectedPass)
            {
                var receipt = await f.Db.WorkTaskIntegrationReceipts.SingleAsync(x => x.WorkItemId == f.Task.Id && x.CandidateCommitSha == commit);
                var publication = await f.Db.SourceControlPublications.SingleAsync(x => x.Id == receipt.PublicationId);
                Assert.NotEqual(publication.CommitSha, commit);
                var workspace=Guid.NewGuid(); var snapshot=await store.PrepareAsync(new(f.Org,f.Repositories[0],workspace,publication.TargetBranch,publication.TicketBranch,commit,"qa"),artifacts);
                var local=PlatformGitWorkspaceClient.LocalWorkspacePath(workspace); localQaPaths.Add(local); var calls=0;
                var comments = new List<CommentOnWorkItemRequest>();
                var runtime=StateRuntime().RegisterCapability<WorkItemReference,WorkItem>(WorkManagementCapabilityNames.ItemRead,(_,_)=>Task.FromResult(Current("quality")))
                    .RegisterCapability<GitWorkspaceSyncRequest,GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability,(_,_)=>Task.FromResult(new GitWorkspaceSyncResult(snapshot.Archive)))
                    .RegisterCapability<InspectGitWorkspaceRequest,GitWorkspaceInspection>(GitWorkspaceCapabilities.Inspect,async (request,ct) => {
                        Assert.Equal(workspace, request.WorkspaceId);
                        Assert.Null(request.PushedCommitSha);
                        var inspection = await store.ApplySnapshotAsync(new(f.Org, publication.RepositoryId, workspace, "inspect",
                            commit, publication.TicketBranch, publication.TargetBranch, "qa-inspect", snapshot.Archive,
                            snapshot.Manifest.Sha256, snapshot.Manifest.FileCount, snapshot.Manifest.TotalBytes), artifacts, ct);
                        Assert.Empty(inspection.ChangedFiles);
                        return new GitWorkspaceInspection(workspace, inspection.Status, false, inspection.ChangedFiles, []);
                    })
                    .RegisterCapability<CleanupGitWorkspaceRequest,GitWorkspaceCleanupResult>(GitWorkspaceCapabilities.Cleanup,(_,_)=>Task.FromResult(new GitWorkspaceCleanupResult(workspace,true,null)))
                    .RegisterCapability<CommentOnWorkItemRequest,JsonElement>(WorkManagementCapabilityNames.ItemComment,(comment,_)=>{
                        comments.Add(comment);
                        return Task.FromResult(Element(new {}));
                    })
                    .RegisterCapability<JsonElement,JsonElement>(PlatformCapabilities.LlmChatStream,async (_,token)=>{
                        if(calls++>0) return Element(new {text="Evidence submitted.",finishReason="stop"});
                        var info=new ProcessStartInfo("node") {WorkingDirectory=local,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        info.ArgumentList.Add("verify.cjs"); using var process=Process.Start(info)!;
                        var output=await process.StandardOutput.ReadToEndAsync(token); var error=await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
                        qaRuns++; Assert.Equal(expectedPass?0:1,process.ExitCode);
                        var passed=process.ExitCode==0;
                        var report=new {summary="Executed the exact integrated fixture test.",passed,
                            validations=new[]{new {command="node verify.cjs",succeeded=passed,exitCode=process.ExitCode,diagnosticExcerpt=output+error}},
                            findings=passed?Array.Empty<string>():new[]{"The integrated implementation fails its executable acceptance test."},
                            criteria=new[]{new {criterion="Requirement works",satisfied=passed,evidence=output+error}}};
                        return Element(new {contents=new[]{new {kind="function_call",callId="report",name="submit_qa_report",arguments=report}},finishReason="tool_calls"});
                    });
                var assignment = Assignment("quality","game-quality-assurance");
                return await RunWithRetainedQaWorkspaceAsync(f, runtime, workspace, publication, commit, assignment, async () =>
                {
                    var outcome = await Execute(runtime, new QaAgent(), assignment);
                    var replay = await Execute(runtime, new QaAgent(), assignment);
                    Assert.Equal(outcome.Output.GetRawText(), replay.Output.GetRawText());
                    var comment = Assert.Single(comments);
                    Assert.Equal(f.Task.Id, comment.ItemId);
                    Assert.Contains(commit, comment.Body);
                    return outcome;
                });
            }
            WorkItem Current(string stage)=>JsonSerializer.Deserialize<WorkItem>("{}")! with {
                Id=f.Task.Id,PlanningRevision=f.Task.PlanningRevision,Planning=planning,
                Delivery=JsonSerializer.Deserialize<WorkItemDeliverySpecification>(f.Task.DeliverySpecificationJson!,Json),
                StageAssignments=[new(stage,"AgentInstallation",AgentInstallationId:Guid.Parse("00000000-0000-0000-0000-000000000001")),
                    new("development","AgentInstallation",AgentInstallationId:Guid.NewGuid())]};
            WorkExecutionAssignment Assignment(string stage,string role)
            {
                var input=new WorkExecutionInputV1(f.Project,f.Board.TeamId!.Value,f.Task.PlanningRevision,planning) {
                    AssignmentRequirements=new(role,[],[],[WorkManagementCapabilityNames.ExecutionRun]),
                    AssignmentSelection=new(Guid.Parse("00000000-0000-0000-0000-000000000001"),1,Hash("profile"),[],Hash("decision"),DateTimeOffset.UtcNow)};
                return JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {
                    Scope="Task",OrganizationId=f.Org,ItemId=f.Task.Id,BoardId=f.Board.Id,SprintId=Guid.NewGuid(),SprintExecutionId=Guid.NewGuid(),PolicyRevisionId=Guid.NewGuid(),
                    ExecutionId=Guid.NewGuid(),StageExecutionId=Guid.NewGuid(),AttemptId=Guid.NewGuid(),Attempt=1,AssignmentRevision=f.Task.AssignmentRevision,
                    PlanningRevision=f.Task.PlanningRevision,DeliveryPlanId=f.PlanId,Deadline=DateTimeOffset.UtcNow.AddMinutes(10),StageKey=stage,Evidence=[],
                    Item=Element(new {id=f.Task.Id,deliverySpecificationJson=f.Task.DeliverySpecificationJson}),Input=Element(input)};
            }
            await HierarchicalTaskRuntimeTests.RunJourney(f,false,publishCandidate:Publish,technicalReview:Review,qualityReview:Quality);
            Assert.True(reviewedPages>=15, $"Reviewed {reviewedPages} pages.");
            Assert.True(correctedPage, "The reviewer must correct the malformed fourth page.");
            Assert.Equal(2,qaRuns);
            Assert.Equal("Completed",(await f.Db.CoreWorkTasks.SingleAsync(x=>x.Id==f.Task.Id)).Status.ToString());
        }
        finally
        {
            foreach(var limit in previous) Environment.SetEnvironmentVariable(limit.Key,limit.Value);
            foreach(var path in localQaPaths)
                if(Path.GetFullPath(path).StartsWith(Path.GetFullPath(PlatformGitWorkspaceClient.LocalWorkspaceRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path,true);
            if(Directory.Exists(root)) {foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal); Directory.Delete(root,true);}
        }
    }

    private static Dictionary<string, string?> SetWorkspaceLimits()
    {
        var limits = new Dictionary<string,string> { ["CSWEET_WORKSPACE_MAXIMUM_ARCHIVE_BYTES"]="16777216",
            ["CSWEET_WORKSPACE_MAXIMUM_EXPANDED_BYTES"]="268435456", ["CSWEET_WORKSPACE_MAXIMUM_FILE_COUNT"]="20000" };
        var previous = limits.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var limit in limits) Environment.SetEnvironmentVariable(limit.Key, limit.Value);
        return previous;
    }

    private static AgentTestRuntime StateRuntime()
    {
        var runtime = new AgentTestRuntime();
        _ = new OperatingStateFixture(runtime);
        return runtime;
    }
    private static async Task<WorkExecutionOutcomeV1> Execute(AgentTestRuntime runtime,ICSweetAgent agent,WorkExecutionAssignment assignment)
    {
        var config=await runtime.ExecuteCapabilityAsync(agent,AgentConfigurationCapabilities.Update,new {settings=new {llmProviderId=Guid.NewGuid(),llmModel="scripted-test"}});
        Assert.True(config.Succeeded,config.Error);
        var host = new AgentTestHost(agent, runtime, businessId: assignment.OrganizationId.ToString());
        var result=await host.ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun,assignment);
        Assert.True(result.Succeeded,result.Error);
        var outcome=result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Json)!;
        Assert.True(outcome.OutcomeCode!="blocked",outcome.Summary); return outcome;
    }
    public class Host:DispatchProxy
    {
        public InternalGitRepositoryStore Store=null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name==nameof(ITrustedSourceControlHostClient.DeliveryBranchAsync)
            ? Store.DeliveryBranchAsync((DeliveryBranchOperation)args![0]!) : throw new NotSupportedException(method.Name);
    }
}
