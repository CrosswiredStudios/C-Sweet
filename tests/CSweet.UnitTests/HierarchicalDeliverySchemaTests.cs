using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class HierarchicalDeliverySchemaTests
{
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
                [new("Release", null, id, [new("quality", "Human", id)])], "configure"),
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

    [Fact]
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
            var capability = manifest.RootElement.GetProperty("provides").EnumerateArray().Single(x => x.GetProperty("name").GetString() == WorkManagementCapabilityNames.ExecutionRunV2);
            JsonSchemaValidator.Validate(outcome, capability.GetProperty("outputSchema"));
            var version = manifest.RootElement.GetProperty("version").GetString()!;
            Assert.Contains(version, File.ReadAllText(Path.Combine(path, "releases", version + ".md")).Split('\n')[0]);
        }
    }
}
