using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class HierarchicalDeliverySchemaTests
{
    [Theory]
    [InlineData(110)]
    [InlineData(150)]
    [InlineData(190)]
    public async Task Generated_workflow_ticket_requests_fit_the_actual_published_create_schema(int keyLength)
    {
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with {
            Id = Guid.NewGuid(), Planning = new(["Implement prototype"], ["Prototype works"])
        };
        var request = new ConfigureWorkDeliveryPlanRequest(Guid.NewGuid(), "Prototype", Guid.NewGuid(), [item.Id], [],
            [new("Release", null, Guid.NewGuid(), [])], new string('p', keyLength));
        var createTool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { WorkItemCapabilities.Create }));
        Assert.Equal(160, createTool.InputSchema.GetProperty("properties").GetProperty("idempotencyKey").GetProperty("maxLength").GetInt32());
        var created = 0;
        var runtime = new CSweet.Agent.SDK.AgentTestRuntime()
            .RegisterCapability<CreateWorkItemRequest, WorkItem>(WorkItemCapabilities.Create, (r, _) => {
                JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(r, new JsonSerializerOptions(JsonSerializerDefaults.Web)), createTool.InputSchema);
                created++;
                return Task.FromResult(item with { Id = Guid.NewGuid(), Planning = r.Planning });
            })
            .RegisterCapability<ConfigureWorkDeliveryPlanRequest, WorkDeliveryPlanResponse>(WorkDeliveryCapabilities.Configure,
                (_, _) => Task.FromResult(JsonSerializer.Deserialize<WorkDeliveryPlanResponse>("{}")!));
        await CSweet.Agent.SDK.TicketWorkflowPlanning.ConfigureAsync(request, [item], runtime.CreateContext(), default);
        Assert.Equal(1, created);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"planId\":\"00000000-0000-0000-0000-000000000001\",\"status\":\"Draft\"}]")]
    public void DeliveryDiscoveryAcceptsSuccessfulListResponses(string response)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { WorkDeliveryCapabilities.Read }));
        McpGatewayEndpoints.ValidateSuccessfulToolOutput(true, JsonSerializer.Deserialize<JsonElement>(response), tool.OutputSchema!.Value);
        Assert.Throws<InvalidOperationException>(() => McpGatewayEndpoints.ValidateSuccessfulToolOutput(
            true, JsonSerializer.SerializeToElement(new { }), tool.OutputSchema.Value));
    }

    [Fact]
    public void TypedDeliveryRequestsMatchThePublishedBrokerSchemas()
    {
        var id = Guid.NewGuid();
        var criteria = new[] { new WorkDeliveryCriterionResult("Works", true, "Observed exact candidate") };
        var requests = new Dictionary<string, object>
        {
            [WorkDeliveryCapabilities.Read] = new ReadWorkDeliveryPlansRequest(id),
            [WorkDeliveryCapabilities.Configure] = new ConfigureWorkDeliveryPlanRequest(id, "Release", id, [id], [],
                [new("Release", null, id, [new("quality", "Human", id)])], "configure")
                { TaskAssignments = [new(id, [new("development", "Human", id), new("quality", "Human", Guid.NewGuid())])] },
            [WorkDeliveryCapabilities.Control] = new ControlWorkDeliveryPlanRequest(id, 1, "activate", "activate"),
            [WorkDeliveryCapabilities.Accept] = new DecideWorkDeliveryAcceptanceRequest(id, id, 1, "digest", true, "Verified", criteria, [], "accept"),
            [WorkDeliveryCapabilities.Recover] = new RecoverWorkDeliveryRequest(id, id, 1, "recover", "Provider restored"),
            [WorkDeliveryCapabilities.Evidence] = new ReadWorkDeliveryEvidenceRequest(id, id),
            [WorkDeliveryCapabilities.Review] = new CompleteWorkDeliveryReviewRequest(id, id, id, 1, new("digest", true, "Verified", criteria, []), "review")
        };
        foreach (var tool in new McpToolCatalog([]).List(requests.Keys.ToHashSet()))
        {
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(requests[tool.Capability], new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        }
    }

    [SiblingAgentRepositoryFact("CreativeDirector.VideoGame", "Producer.VideoGame", "TechnicalDirector.VideoGame", "Engineer.VideoGame", "QA.VideoGame",
        "BuildReleaseEngineer.VideoGame", "ArtDirector.VideoGame", "Artist.VideoGame", "AudioDesigner.VideoGame", "GameDesigner", "LevelDesigner.VideoGame",
        "NarrativeDesigner.VideoGame", "PlaytestResearcher.VideoGame", "TechnicalArtist.VideoGame", "UiUxAccessibilityDesigner.VideoGame",
        "SoftwareProductManager", "SoftwareArchitect", "SoftwareDeveloper", "SoftwareQA")]
    public void EveryAffectedAgentAcceptsRealV2OutcomesAndPublishesVersionMatchedNotes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "CSweet.Api"))) root = root.Parent;
        Assert.NotNull(root);
        var agents = new[] { "CreativeDirector.VideoGame", "Producer.VideoGame", "TechnicalDirector.VideoGame", "Engineer.VideoGame", "QA.VideoGame", "BuildReleaseEngineer.VideoGame",
            "ArtDirector.VideoGame", "Artist.VideoGame", "AudioDesigner.VideoGame", "GameDesigner", "LevelDesigner.VideoGame", "NarrativeDesigner.VideoGame", "PlaytestResearcher.VideoGame",
            "TechnicalArtist.VideoGame", "UiUxAccessibilityDesigner.VideoGame", "SoftwareProductManager", "SoftwareArchitect", "SoftwareDeveloper", "SoftwareQA" };
        var outcome = JsonSerializer.SerializeToElement(new WorkExecutionOutcomeV1(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Completed,
            "passed", "Verified", JsonSerializer.SerializeToElement(new { }), [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var agent in agents)
        {
            var path = Path.Combine(root!.Parent!.FullName, "CSweet.Agent." + agent);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "csweet-plugin.json")));
            var capability = manifest.RootElement.GetProperty("provides").EnumerateArray().Single(x => x.GetProperty("name").GetString() == WorkManagementCapabilityNames.ExecutionRun);
            JsonSchemaValidator.Validate(outcome, capability.GetProperty("outputSchema"));
            var version = manifest.RootElement.GetProperty("version").GetString()!;
            Assert.Contains(version, File.ReadAllText(Path.Combine(path, "releases", version + ".md")).Split('\n')[0]);
        }
    }
}
