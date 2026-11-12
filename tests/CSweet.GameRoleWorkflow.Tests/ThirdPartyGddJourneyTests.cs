extern alias Director;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;
using DirectorAgent = Director::CSweet.Agent.Director.VideoGame.VideoGameDirectorAgent;
using DirectorState = Director::CSweet.Agent.Director.VideoGame.GameDirectorOperatingState;

namespace CSweet.GameRoleWorkflow.Tests;

// The partner is raw JSON plus an artifact, not a built-in agent or a consumer of our CLR DTOs.
// Model judgments are scripted; these tests verify the real review, binding and approval path.
public sealed class ThirdPartyGddJourneyTests
{
    private static readonly JsonSerializerOptions Web=new(JsonSerializerDefaults.Web);
    private static JsonElement E(object value)=>JsonSerializer.SerializeToElement(value,Web);
    private static readonly string[] Evidence=[
        "The creative aim is expressive musical Breakout.",
        "Players feel control and satisfaction as they restore a lost song.",
        "Move a paddle, launch a ball, break bricks, then clear the board.",
        "Neon bursts and reactive chiptune notes respond to impacts.",
        "The Director will observe keyboard play and record feel findings.",
        "Desktop web only; no spending or scope expansion is authorized.",
        "The designer owns later chain tuning before implementation."
    ];

    private static string Document(string format,bool missing=false)
    {
        var rows=Evidence.Where((_,i)=>!missing || i!=4).ToArray();
        return format switch {
            "prose"=>string.Join(" ",rows),
            "table"=>"| Concern | Plan |\n| --- | --- |\n"+string.Join("\n",rows.Select((s,i)=>$"| {i+1} | {s} |")),
            "outline"=>"# Partner Design Proposal\n"+string.Join("\n",rows.Reverse().Select((s,i)=>$"### Partner topic {i+1}\n{s}")),
            "json"=>JsonSerializer.Serialize(new {partner="External Studio",designNotes=rows},Web),
            _=>throw new ArgumentException(format)
        };
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("table")]
    [InlineData("outline")]
    [InlineData("json")]
    public async Task Equivalent_partner_documents_are_accepted_without_house_headings(string format)
    {
        var f=new Fixture(Document(format)); await f.Configure();
        var request=f.Request();
        var result=await f.Agent.HandleCoordinationTurnAsync(request,f.Context,default);
        Assert.Equal("Completed",result.Disposition);
        Assert.Equal(1,f.Models);
        var decision=Assert.Single(f.Decisions);
        Assert.Equal("accepted-with-findings",decision.Disposition);
        Assert.Equal(f.DocumentId,decision.ArtifactId);
        Assert.Equal(f.RevisionId,decision.RevisionId);
        Assert.Equal(f.Hash,decision.RevisionDigest);
        Assert.DoesNotContain(decision.Findings,x=>x.Blocking);
        Assert.Contains("No heading, word count, template",f.SystemPrompt);
        Assert.Contains("gdd_semantic_coverage",f.SchemaName);
        Assert.Equal(f.RevisionId,f.CurrentState.CreativeDesignRevisionId);
        Assert.Equal(1,f.Handoffs);
        Assert.Equal("Completed",(await f.Agent.HandleCoordinationTurnAsync(request,f.Context,default)).Disposition);
        Assert.Equal(1,f.Models); Assert.Single(f.Decisions); Assert.Equal(1,f.Handoffs);
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("table")]
    [InlineData("outline")]
    [InlineData("json")]
    public async Task Actual_missing_coverage_returns_specific_feedback_in_every_structure(string format)
    {
        var f=new Fixture(Document(format,true)) { MissingAcceptance=true }; await f.Configure();
        var result=await f.Agent.HandleCoordinationTurnAsync(f.Request(),f.Context,default);
        Assert.Equal("Continue",result.Disposition);
        Assert.Equal(GameCreativeDesignProtocolV1.FeedbackType,result.Artifact!.Type);
        // A partner can read the response without our DTOs.
        var wire=result.Artifact.Payload;
        Assert.Equal(f.DocumentId,wire.GetProperty("documentId").GetGuid());
        var blocker=Assert.Single(wire.GetProperty("findings").EnumerateArray(),x=>x.GetProperty("blocking").GetBoolean());
        Assert.Equal("gdd.coverage.acceptance-criteria",blocker.GetProperty("code").GetString());
        Assert.Contains("planned acceptance",blocker.GetProperty("requiredFollowUp").GetString());
        Assert.Equal("changes-required",Assert.Single(f.Decisions).Disposition);
        Assert.Null(f.CurrentState.CreativeDesignRevisionId);
        Assert.Equal(0,f.Handoffs);
    }

    [Theory]
    [InlineData("speaker")]
    [InlineData("team")]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("pitch")]
    [InlineData("document-type")]
    public async Task Flexible_content_does_not_relax_authority_or_exact_evidence(string fault)
    {
        var f=new Fixture(Document("prose"),fault); await f.Configure();
        var result=await f.Agent.HandleCoordinationTurnAsync(f.Request(fault),f.Context,default);
        Assert.Equal("Blocked",result.Disposition);
        Assert.Equal(0,f.Models); Assert.Empty(f.Decisions); Assert.Equal(0,f.Handoffs);
    }

    [Fact]
    public async Task Invalid_semantic_judgments_get_one_correction_and_are_not_frozen_as_accepted()
    {
        var f=new Fixture(Document("prose")) { InvalidReview=true }; await f.Configure();
        var request=f.Request();
        var blocked=await f.Agent.HandleCoordinationTurnAsync(request,f.Context,default);
        Assert.Equal("Blocked",blocked.Disposition);
        Assert.Equal(GameCreativeDesignProtocolV1.FailureType,blocked.Artifact!.Type);
        Assert.Equal(GameCreativeDesignFailureCodes.ReviewInvalid,blocked.Artifact.Payload.GetProperty("code").GetString());
        Assert.Equal(2,f.Models); Assert.Empty(f.Decisions); Assert.Equal(0,f.Handoffs);
        Assert.Contains("supporting passage",f.LastPrompt);
        f.InvalidReview=false;
        var recovered=await f.Agent.HandleCoordinationTurnAsync(request with {ExpectedRevision=request.ExpectedRevision+1},f.Context,default);
        Assert.Equal("Completed",recovered.Disposition);
        Assert.Equal(3,f.Models); Assert.Single(f.Decisions);
    }

    [Fact]
    public async Task Missing_review_configuration_blocks_without_deciding_and_can_be_retried_after_configuration()
    {
        var f=new Fixture(Document("prose")); var request=f.Request();
        var blocked=await f.Agent.HandleCoordinationTurnAsync(request,f.Context,default);
        Assert.Equal("Blocked",blocked.Disposition); Assert.Empty(f.Decisions); Assert.Equal(0,f.Models);
        Assert.Equal(GameCreativeDesignFailureCodes.ReviewUnavailable,blocked.Artifact!.Payload.GetProperty("code").GetString());
        await f.Configure();
        Assert.Equal("Completed",(await f.Agent.HandleCoordinationTurnAsync(request,f.Context,default)).Disposition);
        Assert.Single(f.Decisions);
    }

    [Fact]
    public async Task A_previous_platform_rejection_cannot_be_overridden_by_a_new_model_acceptance()
    {
        var f=new Fixture(Document("prose")) { RetainPlatformRejection=true }; await f.Configure();
        var result=await f.Agent.HandleCoordinationTurnAsync(f.Request(),f.Context,default);
        Assert.Equal("Blocked",result.Disposition);
        Assert.Contains("platform did not accept",result.Content);
        Assert.Null(f.CurrentState.CreativeDesignRevisionId);
        Assert.Equal(0,f.Handoffs);
    }

    private sealed class Fixture
    {
        private readonly Guid _director=Guid.NewGuid(),_partner=Guid.NewGuid(),_project=Guid.NewGuid(),_team=Guid.NewGuid(),_session=Guid.NewGuid();
        public Guid DocumentId {get;}=Guid.NewGuid(); public Guid RevisionId {get;}=Guid.NewGuid();
        public string Hash {get;}
        private const string PitchHash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly Dictionary<string,AgentOperatingStateResponse> _states=[];
        private readonly Dictionary<string,PersonalTodoItem> _todos=[];
        private readonly string _stateKey;
        private ArtifactDocument _document;
        public readonly DirectorAgent Agent=new();
        public AgentRuntimeContext Context {get;}
        public List<StructuredArtifactDecisionRequest> Decisions {get;}=[];
        public int Models,Handoffs;
        public bool MissingAcceptance,InvalidReview,RetainPlatformRejection;
        public string? SystemPrompt,SchemaName,LastPrompt;
        public DirectorState CurrentState=>_states[_stateKey].Payload.Deserialize<DirectorState>(Web)!;
        private AgentOperatingStateResponse Saved(string key,JsonElement value)=>new(Guid.NewGuid(),key,"fixture",1,"Active",new Dictionary<string,string>(),[],"fixture",[],Guid.NewGuid(),value,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);

        public Fixture(string content,string? fault=null)
        {
            Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            _stateKey=$"video-game-direction:workstream:{_project:N}";
            var state=new DirectorState {WorkstreamId=_project,TeamId=_team,CreativeDirectorEmployeeId=_partner,
                AcceptedVision=new(1,"pitch","Musical Breakout for desktop web; plan validation and delegate tuning.",Guid.NewGuid(),Guid.NewGuid(),PitchHash,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow)};
            _states[_stateKey]=Saved(_stateKey,E(state));
            _document=new(DocumentId,"External studio design",fault=="document-type" ? "vendor.other.v1" : GameCreativeDesignProtocolV1.DocumentType,"InReview",RevisionId,RevisionId,null,null,null,
                [new(RevisionId,1,null,content,Hash,"Submitted",DateTimeOffset.UtcNow,null,null)]) {WorkstreamId=_project,TeamId=_team};
            var runtime=new AgentTestRuntime()
                .RegisterCapability<JsonElement,PersonalTodoDirectory>(PersonalTodoCapabilities.Read,(_,_)=>Task.FromResult(new PersonalTodoDirectory([],_director)))
                .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(_states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r,_)=>Task.FromResult(_states[r.StateKey]=Saved(r.StateKey,r.Payload)))
                .RegisterCapability<JsonElement,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(_,_)=>Task.FromResult(_document))
                .RegisterCapability<JsonElement,JsonElement>(PlatformCapabilities.LlmChatStream,(r,_)=> {
                    Models++;
                    var messages=r.GetProperty("messages"); SystemPrompt=messages[0].GetProperty("text").GetString();
                    LastPrompt=messages[messages.GetArrayLength()-1].GetProperty("text").GetString();
                    SchemaName=r.GetProperty("responseSchemaName").GetString();
                    object Check(int i)=>new {sufficient=i!=4 || !MissingAcceptance,evidence=i==4 && MissingAcceptance ? "" : InvalidReview ? "Fabricated absent passage" : Evidence[i],
                        reason=i==4 && MissingAcceptance ? "Describe planned acceptance methods and the accountable role." : "The document supplies sufficient planning coverage."};
                    return Task.FromResult(E(new {role="assistant",text=JsonSerializer.Serialize(new {summary="Reviewed the partner document by substance.",
                        creativeIntent=Check(0),playerExperience=Check(1),gameplay=Check(2),presentation=Check(3),acceptanceCriteria=Check(4),constraints=Check(5),openDecisions=Check(6),scopeConcerns=Array.Empty<object>()},Web)})); })
                .RegisterCapability<StructuredArtifactDecisionRequest,ArtifactDocument>(PlatformCapabilities.ArtifactDecideV2,(r,_)=> {
                    Assert.Equal(DocumentId,r.ArtifactId); Assert.Equal(RevisionId,r.RevisionId); Assert.Equal(Hash,r.RevisionDigest);
                    // The production platform rejects accepted + any findings, and acceptance + blockers.
                    Assert.False(r.Disposition=="accepted" && r.Findings.Count>0);
                    if (r.Disposition is "accepted" or "accepted-with-findings") Assert.DoesNotContain(r.Findings,x=>x.Blocking);
                    Decisions.Add(r);
                    var accepted=r.Disposition is "accepted" or "accepted-with-findings" && !RetainPlatformRejection;
                    _document=_document with {AcceptedRevisionId=accepted ? RevisionId : null,
                        Revisions=[_document.Revisions[0] with {Status=accepted ? "Accepted" : "Rejected"}]};
                    return Task.FromResult(_document); })
                .RegisterCapability<AddPersonalTodoItemRequest,PersonalTodoItem>(PersonalTodoCapabilities.Add,(r,_)=> {
                    if (_todos.TryGetValue(r.IdempotencyKey,out var todo)) return Task.FromResult(todo);
                    Handoffs++;
                    todo=new(Guid.NewGuid(),_project,_director,_director,"Marcus",r.Title,r.Description!,"Ready",r.Priority,1,1,null,r.SourceConversationId,r.SourceMessageId,[],null,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
                    _todos[r.IdempotencyKey]=todo; return Task.FromResult(todo); });
            Context=runtime.CreateContext(identity:new(_director.ToString(),"Marcus",null,"Video Game Director",null,[],null,Guid.NewGuid().ToString(),"CEO"));
        }

        public async Task Configure()=>Assert.True((await Agent.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,
            E(new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement> { ["llmProviderId"]=E(Guid.NewGuid()),["llmModel"]=E("review") }))),Context,default)).Succeeded);

        public AgentCoordinationTurnRequest Request(string? fault=null)
        {
            // An external implementation serializes ordinary JSON; it does not instantiate our records.
            var submission=E(new {pitchSha256=fault=="pitch" ? new string('c',64) : PitchHash,documentId=DocumentId,
                revisionId=fault=="revision" ? Guid.NewGuid() : RevisionId,revisionSha256=fault=="digest" ? new string('c',64) : Hash,vendorExtension=new {structure="partner-owned"}});
            return new(_session,2,2,"Partner GDD","Review supplied design",[],new(_director,Guid.NewGuid(),"Marcus","Initiator"),
                new(fault=="speaker" ? Guid.NewGuid() : _partner,Guid.NewGuid(),"Third-party studio agent","Target"),false,
                [new(Guid.NewGuid(),0,_director,"Continue","Prepare design",DateTimeOffset.UtcNow,new(GameCreativeDesignProtocolV1.RequestType,"1.0",PitchHash,1,true,E(new {}),"request")),
                 new(Guid.NewGuid(),1,_partner,"Continue","Design ready for independent review",DateTimeOffset.UtcNow,new(GameCreativeDesignProtocolV1.ResultType,"1.0",PitchHash,1,true,submission,"submission"))])
                {WorkContext=new(Guid.NewGuid(),_project,fault=="team" ? Guid.NewGuid() : _team,null,null,null,null,_session,null,null)};
        }
    }
}
