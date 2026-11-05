extern alias Producer;
extern alias Technical;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;
using ProducerAgent = Producer::CSweet.Agent.Producer.VideoGame.SpecialistAgent;
using Progress = Producer::CSweet.Agent.Producer.VideoGame.ProgressivePlanningProgress;
using TechnicalAgent = Technical::CSweet.Agent.TechnicalDirector.VideoGame.SpecialistAgent;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed class PlanningDirectionJourneyTests
{
    [Fact]
    public async Task Producer_and_Technical_Director_reconcile_direction_once_without_a_new_scope_graph()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var type = typeof(TechnicalAgent);
        var example = type.GetMethod("PlanningContractExample", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var plan = JsonSerializer.SerializeToNode(example)!.AsObject();
        var items = plan["DeliveryItems"]!.AsArray().Take(2).Select(x => x!.DeepClone()).ToArray();
        plan["DeliveryItems"] = new JsonArray(items);
        plan["OpenFeasibilityDecisions"] = new JsonArray("Clarify the idle atmosphere within approved scope.");
        var story = items[1]["ProposalKey"]!.GetValue<string>();
        var scopeNode = new JsonObject { ["Plan"] = plan, ["Coverage"] = JsonSerializer.SerializeToNode(new[] {
            new { SourceKey = "document-0:paragraph-0", ProposalKeys = new[] { story } } }) };
        var scopeType = type.GetNestedType("ProgressiveScopeIndex", BindingFlags.NonPublic)!;
        var scope = scopeNode.Deserialize(scopeType)!;
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope, scopeType)));
        var project = Guid.NewGuid(); var board = Guid.NewGuid(); var team = Guid.NewGuid();
        var cycleData = new { WorkstreamId = project, TeamId = team, TeamRevision = 1, BoardId = board,
            ProfileDigest = new string('a', 64), ApprovedPackageId = Guid.NewGuid(), ApprovedPackageVersion = 1,
            ApprovedPackageDigest = new string('b', 64), LifecyclePhase = "concept", TargetMilestoneKey = "prototype", PlanningFingerprint = new string('c', 64) };
        var technicalCycleType = type.Assembly.GetType("CrosswiredStudios.VideoGame.Contracts.GameProductionPlanningCycleV1")!;
        var producerCycleType = typeof(ProducerAgent).Assembly.GetType(technicalCycleType.FullName!)!;
        var cycleJson = JsonSerializer.Serialize(cycleData);
        var technicalCycle = JsonSerializer.Deserialize(cycleJson, technicalCycleType)!;
        var producerCycle = JsonSerializer.Deserialize(cycleJson, producerCycleType)!;
        var oldDirections = new string('d', 64);
        var key = (string)type.GetMethod("ProgressiveScopeKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [technicalCycle, oldDirections])!;
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        AgentOperatingStateResponse Saved(string name, JsonElement payload) => new(Guid.NewGuid(), name, "video-game.progressive-planning.v1", 1,
            "Active", new Dictionary<string, string>(), [], "scope", [], Guid.NewGuid(), payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        states[key] = Saved(key, JsonSerializer.SerializeToElement(scope, scopeType));
        var technicalRuntime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(states[r.StateKey] = Saved(r.StateKey, r.Payload)));
        var model = new Model(); var technical = new TechnicalAgent(); var starts = 0;
        var person = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Technical Director");
        var member = new AgentTeammate(person.OrganizationUserId.ToString(), "Victor", "Agent", null, null, "Peer", "Online") { AgentInstallationId = person.AgentInstallationId };
        AgentCoordinationSession? retained = null;
        var producerRuntime = new AgentTestRuntime()
            .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(retained!))
            .RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStartBoard, async (r, ct) => {
                starts++;
                var requestType = type.GetNestedType("ProgressivePlanningRequest", BindingFlags.NonPublic)!;
                var request = r.Artifact!.Payload.GetProperty("progressivePlanning").Deserialize(requestType, web)!;
                var method = type.GetMethod("GenerateProgressivePlanningAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var result = await (Task<AgentCoordinationTurnResult>)method.Invoke(technical, [technicalRuntime.CreateContext(), model,
                    new ChatMessage[] { new(ChatRole.User, r.InitialMessage) }, new string[] { "Approved arcade requirement." }, technicalCycle, request, ct])!;
                Assert.NotNull(result.Artifact);
                var artifact = result.Artifact!;
                return retained = new(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty, person, person,
                    r.Subject, r.Objective, [], "Completed", 2, 2, null, false, "Reviewed direction", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    [new(Guid.NewGuid(), 1, person.OrganizationUserId, "Continue", result.Content, DateTimeOffset.UtcNow,
                        new(artifact.Type, artifact.SchemaVersion, artifact.Key, 1, true, artifact.Payload, "reviewed"))]) { BoardSource = new(board) };
            });
        var ensure = typeof(ProducerAgent).GetMethod("EnsurePlanningSessionAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        async Task<AgentCoordinationSession> Reconcile(Guid? existing)
        {
            var named = new Dictionary<string, object?> { ["teammate"] = member, ["boardId"] = board, ["cycle"] = producerCycle,
                ["subject"] = "Direction", ["objective"] = "Keep the idle atmosphere quiet within scope.",
                ["expectedArtifactType"] = "video-game.production.technical-delivery-proposal.v1", ["members"] = Array.Empty<CSweet.WorkManagement.Contracts.ArtifactPackageMemberDigest>(),
                ["managerDirections"] = new[] { "Recorded Director direction: quiet idle, no scope change." },
                ["context"] = producerRuntime.CreateContext(), ["cancellationToken"] = CancellationToken.None,
                ["existingSessionId"] = existing, ["progressivePlanning"] = true,
                ["retainedProgress"] = new Progress(key, digest, oldDirections, []) };
            return await (Task<AgentCoordinationSession>)ensure.Invoke(null, ensure.GetParameters().Select(p => named.GetValueOrDefault(p.Name!, p.DefaultValue)).ToArray())!;
        }
        var first = await Reconcile(null); var second = await Reconcile(first.Id);
        Assert.Equal(first.Id, second.Id); Assert.Equal(1, starts); Assert.Equal(1, model.Calls);
        var output = first.Turns.Last().Artifact!.Payload;
        Assert.Equal(plan["DeliveryItems"]!.ToJsonString(), output.GetProperty("DeliveryItems").GetRawText());
        Assert.Equal(digest, output.GetProperty("progressivePlanning").GetProperty("outlineDigest").GetString());
        Assert.Equal(0, output.GetProperty("progressivePlanning").GetProperty("reviewedAuthorityQuestions").GetArrayLength());
    }

    private sealed class Model : IChatClient
    {
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Assert.Contains("Do not regenerate scope", string.Join("\n", messages.Select(x => x.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"questions":[{"index":0,"disposition":"resolved","ownerRoleKey":"video-game-director","reason":"Recorded direction stays within the approved idle requirement."}],"amendmentRequired":false,"rationale":"No scope change."}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
}
