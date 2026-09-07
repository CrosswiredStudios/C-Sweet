from pathlib import Path
root=Path('../CSweet.Agent.Producer.VideoGame')
(root/'tests'/f'{root.name}.Tests'/'PitchRefinementTests.cs').write_text('''using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class PitchRefinementTests
{
    internal static string Markdown => string.Join("\\n", new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }.Select(x => $"## {x}\\nPlanning detail for {x}."));

    [Fact]
    public async Task QuestionsThenRefinementThenExactApprovalUnlocksPlanningAndRetriesKeepOneDocument()
    {
        var fixture = new Fixture();
        fixture.SeedModel(1, new(false, ["How many levels are in the MVP?"], "Level count affects workload.", Markdown));
        var first = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1), fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Continue, first.Disposition);
        Assert.Equal(0, fixture.PackageCreates); Assert.Equal(0, fixture.Submits);
        var repeated = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1), fixture.Context, default);
        Assert.Equal(first.Artifact!.Payload.GetRawText(), repeated.Artifact!.Payload.GetRawText()); Assert.Equal(1, fixture.Creates);
        var review = first.Artifact.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        fixture.Add(fixture.Producer, first.Artifact, first.Content);
        fixture.Add(fixture.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, fixture.Digest,
            new PitchReply(fixture.Digest, review.DocumentId, review.RevisionId, review.RevisionSha256, false, "One level for the MVP.", Markdown + "\\nOne level.")));
        fixture.SeedModel(3, new(true, [], "One level, local controls and explicit completion criteria are sufficient to size delivery.", Markdown + "\\nOne level."));
        var second = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(3), fixture.Context, default);
        var ready = second.Artifact!.Payload.Deserialize<PitchReview>(PitchProtocol.Json)!;
        Assert.True(ready.Ready); Assert.Equal(1, fixture.Revises); Assert.Equal(1, fixture.Submits); Assert.Equal(0, fixture.PackageCreates);
        Assert.Equal(review.DocumentId, ready.DocumentId); Assert.NotEqual(review.RevisionId, ready.RevisionId);
        fixture.Add(fixture.Producer, second.Artifact, second.Content);
        fixture.Accept(ready);
        fixture.Add(fixture.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, fixture.Digest,
            new PitchReply(fixture.Digest, ready.DocumentId, ready.RevisionId, ready.RevisionSha256, true, "This captures the agreed MVP.", "")));
        var completed = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(5), fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Completed, completed.Disposition);
        Assert.Equal(1, fixture.PackageCreates); Assert.Contains(fixture.PackageMembers!, x => x.ArtifactId == ready.DocumentId && x.AcceptedRevisionId == ready.RevisionId);
        Assert.Equal("video-game.vision.v1", fixture.PackageMembers![0].RequiredDocumentType);
        Assert.Contains(fixture.States.Keys, x => x.Contains("producer"));
    }

    [Fact]
    public async Task UnmatchedApprovalCannotSkipQuestions()
    {
        var fixture = new Fixture();
        fixture.Add(fixture.Director, PitchProtocol.Artifact(PitchProtocol.ReplyType, fixture.Digest,
            new PitchReply(fixture.Digest, Guid.NewGuid(), Guid.NewGuid(), "hash", true, "Approved", "")));
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1), fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition); Assert.Equal(0, fixture.PackageCreates);
    }

    [Fact]
    public async Task FinalizationCannotManufactureReadiness()
    {
        var fixture = new Fixture();
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(fixture.Request(1) with { IsFinalization = true }, fixture.Context, default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition); Assert.Equal(0, fixture.PackageCreates);
    }

    [Fact]
    public void ReadyFlagWithUnansweredQuestionsIsInsufficient() => Assert.False(PitchProtocol.CanPlan(new(true, ["Unknown scope"], "Ready", Markdown)));

    private sealed class Fixture
    {
        public Guid Producer { get; } = Guid.NewGuid(); public Guid Director { get; } = Guid.NewGuid();
        public string Digest => "pitch-digest";
        private Guid Session { get; } = Guid.NewGuid(); private Guid Workstream { get; } = Guid.NewGuid(); private Guid Team { get; } = Guid.NewGuid();
        private List<AgentCoordinationTurn> Transcript { get; } = [];
        private Dictionary<Guid, ArtifactDocument> Documents { get; } = [];
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public AgentRuntimeContext Context { get; }
        public int Creates, Revises, Submits, PackageCreates;
        public IReadOnlyList<ArtifactPackageMember>? PackageMembers;
        public Fixture()
        {
            var document = Guid.NewGuid(); var revision = Guid.NewGuid();
            Documents[document] = new(document, "Pitch", "video-game.vision.v1", "Approved", revision, null, revision, null, null,
                [new(revision, 1, null, "Accepted game pitch", "source-hash", "Accepted", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)]);
            var vision = new GameVisionBrief(Digest, "outcome", "loop", "platform", "art", "MVP", [], "criteria", [])
                { HighLevelGddArtifactId = document, HighLevelGddAcceptedRevisionId = revision, HighLevelGddRevisionSha256 = "source-hash" };
            Add(Director, PitchProtocol.Artifact(PitchProtocol.BriefType, Digest, new PitchBrief(vision, document, revision, "source-hash")));
            var runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead,
                    (request, _) => Task.FromResult(Documents[request.GetProperty("artifactId").GetGuid()]))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(request.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (request, _) => { var saved = State(request.StateKey, request.Payload, (request.ExpectedRevision ?? 0) + 1); States[request.StateKey] = saved; return Task.FromResult(saved); })
                .RegisterCapability<CreateArtifactDocument, ArtifactDocument>(PlatformCapabilities.ArtifactCreate,
                    (request, _) => { Creates++; var id = Guid.NewGuid(); var rev = new ArtifactRevision(Guid.NewGuid(), 1, null, request.Content, "draft1", "Draft", DateTimeOffset.UtcNow, null, null);
                        var doc = new ArtifactDocument(id, request.Title, request.DocumentType, "Draft", rev.Id, null, null, null, null, [rev]) { WorkstreamId = Workstream, TeamId = Team };
                        Documents[id] = doc; return Task.FromResult(doc); })
                .RegisterCapability<CreateArtifactRevision, ArtifactRevision>(PlatformCapabilities.ArtifactRevise,
                    (request, _) => { Revises++; var doc = Documents[request.ArtifactId]; Assert.Equal(doc.LatestRevisionId, request.ExpectedBaseRevisionId);
                        var rev = new ArtifactRevision(Guid.NewGuid(), 2, request.ExpectedBaseRevisionId, request.Content, "draft2", "Draft", DateTimeOffset.UtcNow, null, null);
                        Documents[doc.Id] = doc with { LatestRevisionId = rev.Id, Revisions = [.. doc.Revisions, rev] }; return Task.FromResult(rev); })
                .RegisterCapability<SubmitArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactSubmit,
                    (request, _) => { Submits++; var doc = Documents[request.ArtifactId]; Documents[doc.Id] = doc with { Revisions = doc.Revisions.Select(x => x.Id == request.RevisionId ? x with { Status = "Submitted" } : x).ToList() }; return Task.FromResult(Documents[doc.Id]); })
                .RegisterCapability<CreateArtifactPackage, ArtifactPackage>(PlatformCapabilities.ArtifactPackageCreate,
                    (request, _) => { PackageCreates++; PackageMembers = request.Members; return Task.FromResult(new ArtifactPackage(Guid.NewGuid(), request.Name, request.PackageType, 1, "Draft", request.Members, null)); })
                .RegisterCapability<JsonElement, ArtifactPackage>(PlatformCapabilities.ArtifactPackageSubmit,
                    (request, _) => Task.FromResult(new ArtifactPackage(request.GetProperty("packageId").GetGuid(), "Planning", "planning", 1, "Submitted", PackageMembers!, null)));
            Context = runtime.CreateContext(identity: new AgentIdentity(Producer.ToString(), "Producer", null, "Producer", null, [], null, Director.ToString(), "Creative Director"));
        }
        public void Add(Guid speaker, AgentCoordinationArtifactSubmission artifact, string content = "Clarification") => Transcript.Add(new(Guid.NewGuid(), Transcript.Count,
            speaker, "Continue", content, DateTimeOffset.UtcNow, new(artifact.Type, artifact.SchemaVersion, artifact.Key, 1, true, artifact.Payload, "transport-digest")));
        public AgentCoordinationTurnRequest Request(int turn) => new(Session, turn, turn, "Pitch", "Refine pitch", [],
            new(Producer, Guid.NewGuid(), "Producer", "Producer"), new(Director, Guid.NewGuid(), "Director", "Director"), false, Transcript.ToList())
            { WorkContext = new(Guid.NewGuid(), Workstream, Team, Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) };
        public void SeedModel(int turn, ProducerReview review) { var key = $"pitch-producer:{Session:N}:{turn}"; States[key] = State(key, JsonSerializer.SerializeToElement(review), 1); }
        public void Accept(PitchReview review) { var doc = Documents[review.DocumentId]; Documents[doc.Id] = doc with { AcceptedRevisionId = review.RevisionId,
            Revisions = doc.Revisions.Select(x => x.Id == review.RevisionId ? x with { Status = "Accepted" } : x).ToList() }; }
        private static AgentOperatingStateResponse State(string key, JsonElement payload, long revision) => new(Guid.NewGuid(), key,
            "test", 1, "Active", new Dictionary<string, string>(), [], key, [], Guid.NewGuid(), payload, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}
''',encoding='utf-8')
