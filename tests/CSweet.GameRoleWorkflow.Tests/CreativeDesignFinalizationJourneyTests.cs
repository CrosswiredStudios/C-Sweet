extern alias Director;
extern alias Creative;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using DirectorAgent = Director::CSweet.Agent.Director.VideoGame.VideoGameDirectorAgent;
using DirectorState = Director::CSweet.Agent.Director.VideoGame.GameDirectorOperatingState;
using CreativeAgent = Creative::CSweet.Agent.CreativeDirector.VideoGame.VideoGameCreativeDirectorAgent;

namespace CSweet.GameRoleWorkflow.Tests;

// Real agent callbacks and SDK result validation; deterministic model and persistence substitutes.
public sealed class CreativeDesignFinalizationJourneyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value, Json);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_Gdd_revision_is_corrected_once_or_finalized_as_an_owned_blocker(bool correctionFails)
    {
        var organization = Guid.NewGuid(); var director = Guid.NewGuid(); var naomi = Guid.NewGuid();
        var project = Guid.NewGuid(); var team = Guid.NewGuid(); var session = Guid.NewGuid();
        var pitchId = Guid.NewGuid(); var pitchRevision = Guid.NewGuid(); var pitchHash = new string('a',64);
        var pitch = new ArtifactDocument(pitchId,"Approved pitch","video-game.vision.v1","Approved",pitchRevision,null,pitchRevision,null,null,
            [new(pitchRevision,1,null,"Desktop-web Breakout within the approved pitch",pitchHash,"Accepted",DateTimeOffset.UtcNow,null,null)]);
        var state = new DirectorState { WorkstreamId=project, TeamId=team, CreativeDirectorEmployeeId=naomi,
            AcceptedVision = new(1,"pitch",pitch.Revisions[0].Content,pitchId,pitchRevision,pitchHash,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow) };
        var stores = new Dictionary<Guid,Dictionary<string,AgentOperatingStateResponse>> { [director]=[],[naomi]=[] };
        AgentOperatingStateResponse Saved(string key, JsonElement value) => new(Guid.NewGuid(),key,"fixture",1,"Active",new Dictionary<string,string>(),[],"fixture",[],Guid.NewGuid(),value,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
        stores[director][$"video-game-direction:workstream:{project:N}"] = Saved("project",E(state));
        var sections = new[] {"Creative pillars","Player experience","Gameplay","Presentation","Acceptance criteria","Constraints","Open questions"};
        var valid = string.Join("\n",sections.Select(x=>$"## {x}\nConcrete desktop-web Breakout direction, owned validation and pending decisions."));
        var outputs = new Queue<string>(new[] { valid, "", correctionFails ? " " : valid });
        ArtifactDocument? gdd = null;
        var models=0; var decisions=0; var revisions=0; var submissions=0; var reads=0;
        AgentTestRuntime Runtime(Guid actor) => new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(stores[actor].GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r,_)=>Task.FromResult(stores[actor][r.StateKey]=Saved(r.StateKey,r.Payload)))
            .RegisterCapability<JsonElement,PersonalTodoDirectory>(PersonalTodoCapabilities.Read,(_,_)=>Task.FromResult(new PersonalTodoDirectory([],director)))
            .RegisterCapability<JsonElement,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(r,_)=> {
                reads++; return Task.FromResult(r.GetProperty("artifactId").GetGuid()==pitchId ? pitch : gdd!); })
            .RegisterCapability<JsonElement,JsonElement>(PlatformCapabilities.LlmChatStream,(r,_)=> {
                if (actor==director) {
                    var missing=new {sufficient=false,evidence="",reason="Describe the remaining design and planned acceptance methods."};
                    return Task.FromResult(E(new {role="assistant",text=JsonSerializer.Serialize(new {summary="GDD needs substantive planning coverage.",
                        creativeIntent=missing,playerExperience=missing,gameplay=missing,presentation=missing,acceptanceCriteria=missing,constraints=missing,openDecisions=missing,scopeConcerns=Array.Empty<object>()})}));
                }
                Assert.Equal(naomi,actor); models++;
                if (models==3)
                {
                    Assert.Contains("Prior GDD",r.GetProperty("messages")[1].GetProperty("text").GetString());
                    Assert.Contains("Director findings",r.GetProperty("messages")[1].GetProperty("text").GetString());
                    Assert.Contains("GDD generation could not finish",r.GetProperty("messages")[3].GetProperty("text").GetString());
                }
                return Task.FromResult(E(new {text=outputs.Dequeue(),role="assistant"})); })
            .RegisterCapability<CreateArtifactDocument,ArtifactDocument>(PlatformCapabilities.ArtifactCreate,(r,_)=> {
                var id=Guid.NewGuid(); gdd = new(Guid.NewGuid(),r.Title,r.DocumentType,"Draft",id,null,null,null,null,
                    [new(id,1,null,r.Content,new string('b',64),"Draft",DateTimeOffset.UtcNow,null,null)]) {WorkstreamId=r.WorkstreamId,TeamId=r.TeamId};
                return Task.FromResult(gdd); })
            .RegisterCapability<CreateArtifactRevision,ArtifactRevision>(PlatformCapabilities.ArtifactRevise,(r,_)=> {
                Assert.Equal(gdd!.Id,r.ArtifactId); Assert.Equal(gdd.LatestRevisionId,r.ExpectedBaseRevisionId); revisions++;
                var revision=new ArtifactRevision(Guid.NewGuid(),2,r.ExpectedBaseRevisionId,r.Content,new string('c',64),"Draft",DateTimeOffset.UtcNow,null,null);
                gdd=gdd with {LatestRevisionId=revision.Id,Revisions=[..gdd.Revisions,revision]}; return Task.FromResult(revision); })
            .RegisterCapability<SubmitArtifactRevision,ArtifactDocument>(PlatformCapabilities.ArtifactSubmit,(r,_)=> {
                Assert.Equal(director,r.ReviewerOrganizationUserId); submissions++; return Task.FromResult(gdd!); })
            .RegisterCapability<StructuredArtifactDecisionRequest,ArtifactDocument>(PlatformCapabilities.ArtifactDecideV2,(_,_)=> {
                decisions++; return Task.FromResult(gdd!); });
        var manager = new AgentCoordinationParticipant(director,Guid.NewGuid(),"Marcus","Initiator");
        var creative = new AgentCoordinationParticipant(naomi,Guid.NewGuid(),"Naomi","Target");
        var directorContext=Runtime(director).CreateContext(organization.ToString(),identity:new(director.ToString(),"Marcus",null,"Video Game Director",null,[],null,Guid.NewGuid().ToString(),"CEO"));
        var creativeContext=Runtime(naomi).CreateContext(organization.ToString(),identity:new(naomi.ToString(),"Naomi",null,"Creative Director",null,[],null,director.ToString(),"Video Game Director"));
        var marcus=new DirectorAgent(); var creativeAgent=new CreativeAgent();
        Assert.True((await marcus.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,
            E(new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement> { ["llmProviderId"]=E(Guid.NewGuid()),["llmModel"]=E("review") }))),directorContext,default)).Succeeded);
        Assert.True((await creativeAgent.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,
            E(new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement> { ["llmProviderId"]=E(Guid.NewGuid()),["llmModel"]=E("fixture") }))),creativeContext,default)).Succeeded);
        var workContext=new AgentWorkContext(organization,project,team,null,null,null,null,session,null,null);
        var turns=new List<AgentCoordinationTurn> { new(Guid.NewGuid(),0,director,"Continue","Develop creative direction and GDD",DateTimeOffset.UtcNow,
            new("video-game.creative-design.request.v1","1.0",pitchHash,1,true,E(new {workstreamId=project,teamId=team,directorEmployeeId=director,
                creativeDirectorEmployeeId=naomi,producerEmployeeId=Guid.NewGuid(),pitchArtifactId=pitchId,pitchRevisionId=pitchRevision,pitchSha256=pitchHash}),"request")) };
        AgentCoordinationTurnRequest Request(bool toCreative,bool finalization=false) => new(session,turns.Count,turns.Count,"GDD","Develop exact approved pitch",[],
            toCreative ? creative : manager,toCreative ? manager : creative,finalization,turns.ToArray()) {WorkContext=workContext};
        void Append(Guid speaker,AgentCoordinationTurnResult result) {
            var a=result.Artifact;
            turns.Add(new(Guid.NewGuid(),turns.Count,speaker,result.Disposition,result.Content,DateTimeOffset.UtcNow,
                a is null ? null : new(a.Type,a.SchemaVersion,a.Key,a.PageOrdinal,a.IsFinalPage,a.Payload,"fixture")));
        }
        var submitted=await creativeAgent.HandleCoordinationTurnAsync(Request(true),creativeContext,default);
        Assert.Equal("Continue",submitted.Disposition); Append(naomi,submitted);
        var originalRevision=gdd!.LatestRevisionId;
        var feedback=await marcus.HandleCoordinationTurnAsync(Request(false),directorContext,default);
        Assert.Equal("Continue",feedback.Disposition); Append(director,feedback);
        var revised=await creativeAgent.HandleCoordinationTurnAsync(Request(true),creativeContext,default);
        Assert.Equal(3,models);
        Assert.Equal(correctionFails ? "Blocked" : "Continue",revised.Disposition);
        Assert.Equal(correctionFails ? 0 : 1,revisions);
        Assert.Equal(correctionFails ? 1 : 2,submissions);
        Assert.Equal(1,decisions); // Corrective content never certifies itself.
        if (correctionFails)
        {
            Append(naomi,revised);
            var finalRequest=Request(false,true); var priorReads=reads;
            var final=await marcus.HandleCoordinationTurnAsync(finalRequest,directorContext,default);
            var replay=await marcus.HandleCoordinationTurnAsync(finalRequest,directorContext,default);
            Assert.Equal("Blocked",final.Disposition); Assert.Equal(final,replay);
            Assert.Contains(revised.Content,final.Content);
            Assert.Equal(1,decisions); Assert.Equal(priorReads,reads); Assert.Equal(originalRevision,gdd.LatestRevisionId);
            // Use the actual SDK validator which rejected all three incident attempts.
            var worker=typeof(CSweetAgentBase).Assembly.GetType("CSweet.Agent.SDK.AgentRuntimeWorker`1")!.MakeGenericType(typeof(DirectorAgent));
            worker.GetMethod("ValidateCoordinationResult",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[finalRequest,final]);
            // A human retry of the now-blocked chat returns to Marcus without finalization.
            Append(director,final);
            var resumed=await marcus.HandleCoordinationTurnAsync(Request(false),directorContext,default);
            Assert.Equal("Continue",resumed.Disposition);
            Assert.Equal(feedback.Artifact!.Payload.GetRawText(),resumed.Artifact!.Payload.GetRawText());
            Append(director,resumed);
            outputs.Enqueue(valid);
            var recovered=await creativeAgent.HandleCoordinationTurnAsync(Request(true),creativeContext,default);
            Assert.Equal("Continue",recovered.Disposition);
            Assert.Equal(4,models); Assert.Equal(1,revisions); Assert.Equal(2,submissions); Assert.Equal(1,decisions);
        }
        else
        {
            Assert.Equal(gdd.Id,revised.Artifact!.Payload.GetProperty("documentId").GetGuid());
            Assert.Equal(originalRevision,gdd.Revisions.Last().BaseRevisionId);
            Assert.Equal(gdd.LatestRevisionId,revised.Artifact.Payload.GetProperty("revisionId").GetGuid());
        }
    }
}
