extern alias Director;
extern alias Creative;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using DirectorAgent = Director::CSweet.Agent.Director.VideoGame.VideoGameDirectorAgent;
using DirectorState = Director::CSweet.Agent.Director.VideoGame.GameDirectorOperatingState;
using CreativeAgent = Creative::CSweet.Agent.CreativeDirector.VideoGame.VideoGameCreativeDirectorAgent;

namespace CSweet.GameRoleWorkflow.Tests;

// Real role callbacks over the SDK broker; model output and host persistence are deterministic fixtures.
public sealed class CreativeHandoffJourneyTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static JsonElement E(object value) => JsonSerializer.SerializeToElement(value, Json);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Producer_brief_waits_for_exact_Naomi_review_then_Marcus_accepts_and_replay_is_safe(bool changesRequired, bool requiresDirector, bool separateSources)
    {
        var organization = Guid.NewGuid(); var director = Guid.NewGuid(); var naomi = Guid.NewGuid(); var producer = Guid.NewGuid();
        var project = Guid.NewGuid(); var team = Guid.NewGuid(); var parentId = Guid.NewGuid(); var childId = Guid.NewGuid();
        var pitchId = Guid.NewGuid(); var pitchRevision = Guid.NewGuid(); var documentId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
        var pitchHash = new string('a', 64); var hash = new string('b', 64);
        var markdown = string.Join("\n", new[] {"Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions"}.Select(x => $"## {x}\nSingle-player keyboard arcade game in Chromium; no network or paid assets."));
        var pitch = new ArtifactDocument(pitchId, "Official game pitch", "video-game.vision.v1", "Approved", pitchRevision, null, pitchRevision, null, null,
            [new(pitchRevision, 1, null, "Collect ten stars in a browser arcade demo. Keyboard controls, no network, no paid assets.", pitchHash, "Accepted", DateTimeOffset.UtcNow, null, null)]);
        var brief = new ArtifactDocument(documentId, "Production brief", "video-game.production-plan.v1", "InReview", revisionId, revisionId, null, null, null,
            [new(revisionId, 1, null, markdown, hash, "Submitted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = project, TeamId = team };
        var gddId = Guid.NewGuid(); var gddRevision = Guid.NewGuid(); var sourceId = Guid.NewGuid(); var sourceRevision = Guid.NewGuid();
        var gdd = new ArtifactDocument(gddId, "GDD", "video-game.gdd.v1", "Approved", gddRevision, null, gddRevision, null, null,
            [new(gddRevision,1,null,"Keyboard alternative and readable impacts",hash,"Accepted",DateTimeOffset.UtcNow,null,null)]) { WorkstreamId=project, TeamId=team };
        var sourceContent = E(new { version=1,workstreamId=project,teamId=team,
            pitch=new {documentId=pitchId,revisionId=pitchRevision,sha256=pitchHash,documentType=pitch.DocumentType,content=pitch.Revisions[0].Content},
            gdd=new {documentId=gddId,revisionId=gddRevision,sha256=hash,documentType=gdd.DocumentType,content=gdd.Revisions[0].Content} }).GetRawText();
        var sources = new ArtifactDocument(sourceId,"Exact sources","video-game.production-sources.v1","InReview",sourceRevision,sourceRevision,null,null,null,
            [new(sourceRevision,1,null,sourceContent,hash,"Submitted",DateTimeOffset.UtcNow,null,null)]) { WorkstreamId=project, TeamId=team };
        CollaborationDocumentReference? sourceReference = separateSources ? new(sourceId,sourceRevision,hash) : null;
        var state = new DirectorState { WorkstreamId = project, TeamId = team, ProducerEmployeeId = producer, CreativeDirectorEmployeeId = naomi,
            CreativeDesignArtifactId=gddId, CreativeDesignRevisionId=gddRevision,
            AcceptedVision = new(1, "pitch", pitch.Revisions[0].Content, pitchId, pitchRevision, pitchHash, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var stores = new Dictionary<Guid, Dictionary<string, AgentOperatingStateResponse>> { [director] = [], [naomi] = [] };
        AgentOperatingStateResponse Saved(string key, JsonElement payload) => new(Guid.NewGuid(), key, "fixture", 1, "Active", new Dictionary<string,string>(), [], key, [], Guid.NewGuid(), payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        stores[director][$"video-game-direction:workstream:{project:N}"] = Saved("project", E(state));
        StartAgentCoordinationRequest? start = null; var resumes = 0; var decisions = 0; var creativeModels = 0; var directorModels = 0;
        var manager = new AgentCoordinationParticipant(director, Guid.NewGuid(), "Marcus", "Initiator");
        var creative = new AgentCoordinationParticipant(naomi, Guid.NewGuid(), "Naomi", "Target");
        var parent = new AgentCoordinationSession(parentId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), manager,
            new(producer, Guid.NewGuid(), "Gabriel", "Target"), "Production brief", "Plan", [], "Blocked", 3, 3, director, false, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [new(Guid.NewGuid(), 1, producer, "Continue", "Ready", DateTimeOffset.UtcNow,
                new("video-game.production.pitch-review.v1", "1.0", "pitch", 1, true, E(new { pitchDigest = "pitch", documentId, revisionId,
                    revisionSha256 = hash, ready = true, questions = Array.Empty<string>(), rationale = "Ready" }), "review")),
                new(Guid.NewGuid(), 2, director, "Blocked", "Awaiting Naomi's creative review of the exact production brief.", DateTimeOffset.UtcNow)]);
        var context = new AgentWorkContext(organization, project, team, null, null, null, null, parentId, null, null);
        parent = parent with { WorkContext = context };
        AgentTestRuntime Runtime(Guid actor) => new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(stores[actor].GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => Task.FromResult(stores[actor][r.StateKey] = Saved(r.StateKey, r.Payload)))
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (r, _) => Task.FromResult(
                r.GetProperty("artifactId").GetGuid() == pitchId ? pitch : r.GetProperty("artifactId").GetGuid() == gddId ? gdd :
                r.GetProperty("artifactId").GetGuid() == sourceId ? sources : brief))
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1, (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<StartAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStart, (r, _) => {
                Assert.Equal(naomi, r.TargetOrganizationUserId); start = r;
                return Task.FromResult(parent with { Id = childId, Initiator = manager, Target = creative, Status = "Active", Turns = [] }); })
            .RegisterCapability<JsonElement, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(parent))
            .RegisterCapability<JsonElement, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume, (_, _) => { resumes++; return Task.FromResult(parent with { Status = "Active" }); })
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => Task.FromResult(new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), director, director, "Marcus", r.Title, r.Description!, "Ready", r.Priority, 1, 1, null, r.SourceConversationId, r.SourceMessageId, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory([], director)))
            .RegisterCapability<DecideArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactDecide, (r, _) => {
                Assert.Equal(director, actor); decisions++;
                if (r.ArtifactId == sourceId) { Assert.Equal(sourceRevision,r.RevisionId);
                    sources=sources with {AcceptedRevisionId=sourceRevision,Revisions=[sources.Revisions[0] with {Status="Accepted"}]}; return Task.FromResult(sources); }
                Assert.Equal(revisionId, r.RevisionId);
                brief = brief with { AcceptedRevisionId = revisionId, Revisions = [brief.Revisions[0] with { Status = "Accepted" }] }; return Task.FromResult(brief); })
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                var system = r.GetProperty("messages")[0].GetProperty("text").GetString()!;
                if (actor == naomi) { creativeModels++; Assert.Contains("independent-reviewer", system);
                    if (separateSources) Assert.Contains("Keyboard alternative and readable impacts",r.GetProperty("messages")[1].GetProperty("text").GetString());
                    return Task.FromResult(E(new { text = JsonSerializer.Serialize(new { disposition = changesRequired ? "changes-required" : "accepted", summary = "Reviewed exact player experience.", findings = changesRequired ? new[]{"Player promise changed"} : new[]{"The approved player promise is preserved"}, requiredChanges = changesRequired ? new[]{"Restore the approved one-arena scope"} : [], requiresDirector,
                        checklistVersion = "production-brief.v1",
                        checklist = new[] {"scope","player-loop","controls-accessibility","presentation-audio","validation-plan","constraints-non-goals"}
                            .Select(id => new {id, status = changesRequired && id == "scope" ? "blocking" : "passed", evidence = "Checked exact approved scope"}),
                        issues = changesRequired ? new[] { new {id="scope-arena",checkId="scope",severity="blocking",status="open",requirement="Approved one-arena scope",evidence="Brief changes player promise",owner="game-producer",milestone="pre-production",change="Restore the approved one-arena scope"}} : [] }), role = "assistant" })); }
                directorModels++; Assert.Contains("production-baseline-review", system);
                return Task.FromResult(E(new { text = "{\"accept\":true,\"guidance\":\"Approved scope and budget are preserved.\",\"suggestedMarkdown\":\"\",\"requiresOwner\":false}", role = "assistant" }));
            });
        var directorContext = Runtime(director).CreateContext(organization.ToString(), identity: new(director.ToString(), "Marcus", null, "Video Game Director", null, [], null, Guid.NewGuid().ToString(), "CEO"));
        var creativeContext = Runtime(naomi).CreateContext(organization.ToString(), identity: new(naomi.ToString(), "Naomi", null, "Creative Director", null, [], null, director.ToString(), "Marcus"));
        var marcus = new DirectorAgent(); var creativeAgent = new CreativeAgent();
        foreach (var pair in new (CSweetAgentBase Agent, AgentRuntimeContext Context)[] { (marcus, directorContext), (creativeAgent, creativeContext) })
            Assert.True((await pair.Agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update, E(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> { ["llmProviderId"] = E(Guid.NewGuid()), ["llmModel"] = E("fixture") }))), pair.Context, default)).Succeeded);
        var producerReview = new AgentCoordinationArtifact("video-game.production.pitch-review.v1", "1.0", "pitch", 1, true, E(new { pitchDigest = "pitch", documentId, revisionId, revisionSha256 = hash, ready = true, questions = Array.Empty<string>(), rationale = "Ready for delivery planning",sourceEvidence=sourceReference }), "review");
        var request = new AgentCoordinationTurnRequest(parentId, 2, 2, "Brief", "Review", [], manager, parent.Target, false,
            [new(Guid.NewGuid(), 1, producer, "Continue", "Ready", DateTimeOffset.UtcNow, producerReview)]) { WorkContext = context };
        var blocked = await marcus.HandleCoordinationTurnAsync(request, directorContext, default);
        Assert.Equal("Blocked", blocked.Disposition); Assert.Equal(0, decisions); Assert.Equal(0, directorModels); Assert.NotNull(start);
        var initial = start!.Artifact!;
        var transcript = new List<AgentCoordinationTurn> { new(Guid.NewGuid(), 0, director, "Continue", "Review", DateTimeOffset.UtcNow,
            new(initial.Type, initial.SchemaVersion, initial.Key, initial.PageOrdinal, initial.IsFinalPage, initial.Payload, "request")) };
        var child = new AgentCoordinationTurnRequest(childId, 1, 1, "Review", "Creative consistency", [], creative, manager, false, transcript) { WorkContext = context };
        var reviewed = await creativeAgent.HandleCoordinationTurnAsync(child, creativeContext, default);
        Assert.Equal("Continue", reviewed.Disposition);
        var result = reviewed.Artifact!;
        transcript.Add(new(Guid.NewGuid(), 1, naomi, "Continue", reviewed.Content, DateTimeOffset.UtcNow,
            new(result.Type, result.SchemaVersion, result.Key, result.PageOrdinal, result.IsFinalPage, result.Payload, "result")));
        var returned = await marcus.HandleCoordinationTurnAsync(child with { Self = manager, Counterpart = creative, Transcript = transcript }, directorContext, default);
        Assert.Equal("Completed", returned.Disposition); Assert.Equal(1, resumes);
        var final = await marcus.HandleCoordinationTurnAsync(request, directorContext, default);
        Assert.Equal("Continue", final.Disposition); Assert.Equal(!changesRequired, final.Artifact!.Payload.GetProperty("accepted").GetBoolean());
        await marcus.HandleCoordinationTurnAsync(request, directorContext, default);
        Assert.Equal(changesRequired ? 0 : separateSources ? 2 : 1, decisions); Assert.Equal(changesRequired && !requiresDirector ? 0 : 1, directorModels); Assert.Equal(1, creativeModels);
        if (changesRequired) Assert.NotEqual(JsonValueKind.Null, final.Artifact.Payload.GetProperty("correction").ValueKind);
    }
}
