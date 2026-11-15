extern alias Technical;
extern alias Producer;
using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using TechnicalAgent=Technical::CSweet.Agent.TechnicalDirector.VideoGame.SpecialistAgent;
using ProducerAgent=Producer::CSweet.Agent.Producer.VideoGame.SpecialistAgent;
namespace CSweet.GameRoleWorkflow.Tests;

public sealed class PublicRefinementJourneyTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData("Classic Breakout","game-engineer")]
    [InlineData("Interacting power-ups","game-designer")]
    [InlineData("Turn-based tactics","external-tactics-specialist")]
    public async Task ActualTechnicalAndProducerCallbacksUseTheSamePublicProtocolWithoutRequiringASharedRoleLayout(string outcome,string role)
    {
        var project=Guid.NewGuid();var board=Guid.NewGuid();var document=Guid.NewGuid();var revision=Guid.NewGuid();
        var owner=new AgentCoordinationParticipant(Guid.NewGuid(),Guid.NewGuid(),"Plan owner","Producer");
        var contributor=new AgentCoordinationParticipant(Guid.NewGuid(),Guid.NewGuid(),"Qualified contributor","External capable specialist");
        var input=new WorkRefinementRequest(project,board,"Refine only the approaching approved outcome",new(document,revision,"approved"),
            [new("Epic","general.epic.v1",false,null),new("Story","general.story.v1",false,"Epic"),new("Task","general.task.v1",true,"Story")],
            [new(role,"Artifact","author")],["Preserve accessibility and platform constraints"]);
        var draft=new WorkRefinementDraft("The workload for "+outcome+" calls for "+role+" for its approaching deliverable",[
            new("Epic",outcome,"Approved outcome",["The approved outcome is verifiable"],null,[],null),
            new("Story","Approaching slice","Independent scope",["The approved outcome is verifiable"],0,[],null),
            new("Task","Deliver the slice","Implementation and exact document evidence",["The approved outcome is verifiable"],1,[],role)]);
        var states=new Dictionary<string,AgentOperatingStateResponse>();var calls=0;
        var runtime=new AgentTestRuntime()
            .RegisterCapability<WorkBoardReference,WorkBoardDetail>(WorkItemCapabilities.Read,(_,_)=>Task.FromResult(JsonSerializer.Deserialize<WorkBoardDetail>("{}")! with {
                Board=JsonSerializer.Deserialize<WorkBoardSummary>("{}")! with {Id=board,WorkstreamId=project,ManagerOrganizationUserId=owner.OrganizationUserId},Items=[]}))
            .RegisterCapability<object,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(_,_)=>Task.FromResult(new ArtifactDocument(document,"Approved "+outcome,"scope","Accepted",revision,null,revision,null,null,
                [new(revision,1,null,"Deliver "+outcome+". Preserve accessibility and platform constraints.","approved","Accepted",DateTimeOffset.UtcNow,null,null)]) {WorkstreamId=project}))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=>Task.FromResult(states[r.StateKey]=new(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement,object>(PlatformCapabilities.LlmChatStream,(r,_)=> {calls++;return Task.FromResult<object>(new {text=JsonSerializer.Serialize(draft,Json)});});
        var agent=new TechnicalAgent();
        await (Task)typeof(CSweetAgentBase).GetMethod("ApplyPlatformConfigurationAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(agent,
            [new AgentRuntimeConfiguration("1.0",new Dictionary<string,JsonElement> {["llmProviderId"]=JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),["llmModel"]=JsonSerializer.SerializeToElement("scripted-review")}),null,CancellationToken.None])!;
        var transcript=new List<AgentCoordinationTurn>{new(Guid.NewGuid(),0,owner.OrganizationUserId,"Continue","Scoped request",DateTimeOffset.UtcNow,
            new(ScopedWorkRefinement.RequestType,"1.0","public-refinement-question",1,true,JsonSerializer.SerializeToElement(input,Json),"request-digest"))};
        var request=new AgentCoordinationTurnRequest(Guid.NewGuid(),1,1,"Refine approved work","Source-bound planning",[],contributor,owner,false,transcript) {
            SourceKind="Board",BoardSource=new(board),WorkContext=new(Guid.NewGuid(),project,null,board,null,null,null,Guid.NewGuid(),null,null)};
        var answer=await agent.HandleCoordinationTurnAsync(request,runtime.CreateContext(),default);
        Assert.Equal("Completed",answer.Disposition);Assert.Equal(ScopedWorkRefinement.AnswerType,answer.Artifact!.Type);
        Assert.Equal(answer.Artifact.Payload.GetRawText(),(await agent.HandleCoordinationTurnAsync(request,runtime.CreateContext(),default)).Artifact!.Payload.GetRawText());
        Assert.Equal(1,calls);Assert.Contains(role,answer.Artifact.Payload.GetRawText());
        transcript.Add(new(Guid.NewGuid(),1,contributor.OrganizationUserId,"Completed",answer.Content,DateTimeOffset.UtcNow,
            new(answer.Artifact.Type,"1.0",answer.Artifact.Key,1,true,answer.Artifact.Payload,"answer-digest")));
        var received=await new ProducerAgent().HandleCoordinationTurnAsync(request with {Self=owner,Counterpart=contributor,IsFinalization=true,TurnOrdinal=2},runtime.CreateContext(),default);
        Assert.Equal("Completed",received.Disposition);Assert.Equal(1,calls);
        // No hire, approval, delivery acceptance or board-mutation capability was registered: advice alone grants none.
    }
}
