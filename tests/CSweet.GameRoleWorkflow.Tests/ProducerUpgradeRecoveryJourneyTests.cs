extern alias Director;
extern alias Producer;
using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using DirectorAgent=Director::CSweet.Agent.Director.VideoGame.VideoGameDirectorAgent;
using DirectorState=Director::CSweet.Agent.Director.VideoGame.GameDirectorOperatingState;
using ProducerAgent=Producer::CSweet.Agent.Producer.VideoGame.SpecialistAgent;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed class ProducerUpgradeRecoveryJourneyTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static JsonElement E(object value)=>JsonSerializer.SerializeToElement(value,Json);

    [Fact]
    public async Task UpgradedDirectorResumesOldFourDraftBlockAndActualProducerRevisesWithoutNewApproval()
    {
        var director=Guid.NewGuid();var producer=Guid.NewGuid();var project=Guid.NewGuid();var team=Guid.NewGuid();var org=Guid.NewGuid();
        var sessionId=Guid.NewGuid();var pitchId=Guid.NewGuid();var pitchRevision=Guid.NewGuid();var gddId=Guid.NewGuid();var gddRevision=Guid.NewGuid();
        var document=Guid.NewGuid();var revision=Guid.NewGuid();var hash=new string('a',64);
        var states=new Dictionary<Guid,Dictionary<string,AgentOperatingStateResponse>>{[director]=[],[producer]=[]};
        var documents=new Dictionary<Guid,ArtifactDocument>();
        var body=string.Join("\n",new[]{"Scope","Player experience","Non-goals","Acceptance criteria","Deliverables","Constraints","Risks","Open questions"}.Select(x=>$"## {x}\nExplicit approved arcade scope"));
        ArtifactDocument Doc(Guid id,Guid rev,string type,string content,string status)=>new(id,type,type,status,rev,status=="Submitted"?rev:null,status=="Accepted"?rev:null,null,null,
            [new(rev,id==document?4:1,null,content,hash,status,DateTimeOffset.UtcNow,null,null)]){WorkstreamId=project,TeamId=team};
        documents[pitchId]=Doc(pitchId,pitchRevision,"video-game.vision.v1","Accepted arcade promise","Accepted");
        documents[gddId]=Doc(gddId,gddRevision,"video-game.gdd.v1","Independent flash and reduced-motion controls","Accepted");
        documents[document]=Doc(document,revision,"video-game.production-plan.v1",body,"Submitted");
        var manager=new AgentCoordinationParticipant(director,Guid.NewGuid(),"Marcus","Director");
        var target=new AgentCoordinationParticipant(producer,Guid.NewGuid(),"Gabriel","Producer");
        var work=new AgentWorkContext(org,project,team,null,null,null,null,sessionId,null,null);
        AgentCoordinationArtifact A(string type,object payload)=>new(type,"1.0","pitch-digest",1,true,E(payload),"transport");
        var turns=new List<AgentCoordinationTurn>{new(Guid.NewGuid(),0,director,"Continue","Refine exact accepted sources",DateTimeOffset.UtcNow,A("video-game.production.pitch-brief.v1",new {
            pitchArtifactId=pitchId,pitchRevisionId=pitchRevision,pitchSha256=hash,vision=new {
                acceptedPitchDigest="pitch-digest",playerAndProductOutcome="Arcade promise",gameplayLoopAndCreativePillars="Readable impact",
                platformAndStackConstraints="Chromium",artNarrativeAudioAndToneDirection="Arcade",mvpScopeAndNonGoals="One playfield",
                referenceSummaries=Array.Empty<string>(),successCriteriaRisksAndAssumptions="Observe controls",openDecisions=Array.Empty<string>(),
                highLevelGddArtifactId=gddId,highLevelGddAcceptedRevisionId=gddRevision,highLevelGddRevisionSha256=hash}}))};
        for(var i=0;i<4;i++)turns.Add(new(Guid.NewGuid(),1+i*3,producer,"Continue","Ready",DateTimeOffset.UtcNow,A("video-game.production.pitch-review.v1",new {
            pitchDigest="pitch-digest",documentId=document,revisionId=revision,revisionSha256=hash,ready=true,questions=Array.Empty<string>(),rationale="No remaining planning questions"})));
        turns.Add(new(Guid.NewGuid(),12,director,"Continue","Correct the retained findings",DateTimeOffset.UtcNow,A("video-game.production.pitch-reply.v1",new {
            pitchDigest="pitch-digest",documentId=document,revisionId=revision,revisionSha256=hash,accepted=false,guidance="Record separate flash and reduced-motion controls",suggestedMarkdown=""})));
        turns.Add(new(Guid.NewGuid(),13,producer,"Blocked","Planning refinement needs a concrete manager answer or a new recorded owner decision. The exact draft is retained.",DateTimeOffset.UtcNow));
        turns.Add(new(Guid.NewGuid(),14,director,"Blocked","This request needs a supported project-scoped artifact.",DateTimeOffset.UtcNow));
        var session=new AgentCoordinationSession(sessionId,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),manager,target,"Production brief","Refine",[],"Blocked",19,15,null,true,
            turns[^1].Content,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,turns){WorkContext=work};
        var state=new DirectorState{WorkstreamId=project,TeamId=team,ProducerEmployeeId=producer,CreativeDirectorEmployeeId=Guid.NewGuid(),HandoffSessionId=sessionId,
            CreativeDesignArtifactId=gddId,CreativeDesignRevisionId=gddRevision,AcceptedVision=new(1,"pitch-digest","Accepted arcade promise",pitchId,pitchRevision,hash,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow)};
        AgentOperatingStateResponse Saved(string key,JsonElement value,long rev=1)=>new(Guid.NewGuid(),key,"test",1,"Active",new Dictionary<string,string>(),[],"test",[],Guid.NewGuid(),value,rev,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
        states[director][$"video-game-direction:workstream:{project:N}"]=Saved("project",E(state));
        states[director][$"director-creative-review:{project:N}:{revision:N}:{pitchRevision:N}"]=Saved("review",E(new {
            request=new {workstreamId=project,teamId=team,pitchArtifactId=pitchId,pitchRevisionId=pitchRevision,pitchSha256=hash,documentId=document,revisionId=revision,sha256=hash,
                purpose="Review creative consistency and player experience within the approved pitch.",parentSessionId=sessionId},
            review=new {disposition="changes-required",summary="Clarify accessibility controls",findings=new[]{"Both toggles are required"},requiredChanges=new[]{"Record separate flash and reduced-motion controls"},requiresDirector=false}}));
        var resumes=0;var revisions=0;var sourceCreates=0;
        AgentTestRuntime Runtime(Guid actor)=>new AgentTestRuntime()
            .RegisterCapability<ReadAgentCoordinationRequest,AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,(_,_)=>Task.FromResult(session))
            .RegisterCapability<ReadDecisionRequest,IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,(_,_)=>Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<JsonElement,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(r,_)=>Task.FromResult(documents[r.GetProperty("artifactId").GetGuid()]))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states[actor].GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=>Task.FromResult(states[actor][r.StateKey]=Saved(r.StateKey,r.Payload,(r.ExpectedRevision??0)+1)))
            .RegisterCapability<ResumeAgentCoordinationRequest,AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume,(r,_)=>{
                Assert.Equal(director,actor);Assert.Equal(session.Revision,r.ExpectedRevision);resumes++;
                return Task.FromResult(session=session with {Status="Active",Revision=session.Revision+1,IsFinalization=false});})
            .RegisterCapability<CreateArtifactDocument,ArtifactDocument>(PlatformCapabilities.ArtifactCreate,(r,_)=>{
                Assert.Equal(producer,actor);Assert.Equal("video-game.production-sources.v1",r.DocumentType);sourceCreates++;
                var d=Doc(Guid.NewGuid(),Guid.NewGuid(),r.DocumentType,r.Content,"Draft");documents.Add(d.Id,d);return Task.FromResult(d);})
            .RegisterCapability<CreateArtifactRevision,ArtifactRevision>(PlatformCapabilities.ArtifactRevise,(r,_)=>{
                Assert.Equal(producer,actor);Assert.Equal(revision,r.ExpectedBaseRevisionId);revisions++;
                var next=new ArtifactRevision(Guid.NewGuid(),5,revision,r.Content,new string('b',64),"Draft",DateTimeOffset.UtcNow,null,null);
                documents[document]=documents[document] with {LatestRevisionId=next.Id,Revisions=[..documents[document].Revisions,next]};return Task.FromResult(next);})
            .RegisterCapability<SubmitArtifactRevision,ArtifactDocument>(PlatformCapabilities.ArtifactSubmit,(r,_)=>{
                var d=documents[r.ArtifactId];return Task.FromResult(documents[d.Id]=d with {SubmittedRevisionId=r.RevisionId,Revisions=d.Revisions.Select(x=>x.Id==r.RevisionId?x with {Status="Submitted"}:x).ToArray()});});
        var directorContext=Runtime(director).CreateContext(org.ToString(),identity:new(director.ToString(),"Marcus",null,"Director",null,[],null,Guid.NewGuid().ToString(),"CEO"));
        var producerContext=Runtime(producer).CreateContext(org.ToString(),identity:new(producer.ToString(),"Gabriel",null,"Producer",null,[],null,director.ToString(),"Director"));
        // Invoke the real attention-reconciliation entry, then deliver the platform's resumed initiator callback.
        var check=typeof(DirectorAgent).GetMethod("CheckProducerHandoffAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        await (Task)check.Invoke(null,[state,directorContext,CancellationToken.None])!;
        Assert.Equal(1,resumes);
        var resumed=new AgentCoordinationTurnRequest(sessionId,session.Revision,15,session.Subject,session.Objective,[],manager,target,false,turns){WorkContext=work};
        var directive=await new DirectorAgent().HandleCoordinationTurnAsync(resumed,directorContext,default);
        Assert.Equal("Continue",directive.Disposition);Assert.False(directive.Artifact!.Payload.GetProperty("accepted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null,directive.Artifact.Payload.GetProperty("correction").ValueKind);
        var artifact=directive.Artifact;
        turns.Add(new(Guid.NewGuid(),15,director,"Continue",directive.Content,DateTimeOffset.UtcNow,
            new(artifact.Type,artifact.SchemaVersion,artifact.Key,1,true,artifact.Payload,"trusted-result")));
        states[producer][$"pitch-producer:{sessionId:N}:16"]=Saved("producer-output",E(new {
            ready=true,questions=Array.Empty<string>(),rationale="Both accessibility controls are now explicit",draftMarkdown=body+"\nScreen-flash toggle and reduced-motion mode are separate required controls."}));
        var nextRequest=new AgentCoordinationTurnRequest(sessionId,session.Revision+1,16,session.Subject,session.Objective,[],target,manager,false,turns){WorkContext=work};
        var producerAgent=new ProducerAgent();
        var corrected=await producerAgent.HandleCoordinationTurnAsync(nextRequest,producerContext,default);
        Assert.Equal("Continue",corrected.Disposition);Assert.True(corrected.Artifact!.Payload.GetProperty("ready").GetBoolean());
        Assert.Equal(1,revisions);Assert.Equal(1,sourceCreates);Assert.Equal(document,corrected.Artifact.Payload.GetProperty("documentId").GetGuid());
        Assert.DoesNotContain("Accepted arcade promise",documents[document].Revisions.Last().Content);
        var replay=await producerAgent.HandleCoordinationTurnAsync(nextRequest,producerContext,default);
        Assert.Equal(corrected.Artifact.Payload.GetRawText(),replay.Artifact!.Payload.GetRawText());Assert.Equal(1,revisions);Assert.Equal(1,sourceCreates);
        // No model, hiring, spending, artifact-decision or package capability was registered.
    }
}
