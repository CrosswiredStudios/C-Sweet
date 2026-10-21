using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class GitReviewSdkSchemaTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Assignment_history_request_matches_inspection_schema_and_rejects_arbitrary_refs()
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { GitWorkspaceCapabilities.Inspect }));
        var request = new InspectGitWorkspaceRequest(Guid.NewGuid(), 3) { IncludeHistory = true, HistoryOffset = 20, HistoryLimit = 20 };
        var payload = JsonSerializer.SerializeToNode(request, Json)!.AsObject();
        Assert.Null(McpGatewayEndpoints.ValidateToolInput("history", JsonSerializer.SerializeToElement(payload, Json), tool.InputSchema));
        payload["commitSha"] = new string('a', 40);
        Assert.NotNull(McpGatewayEndpoints.ValidateToolInput("history", JsonSerializer.SerializeToElement(payload, Json), tool.InputSchema));
        payload.Remove("commitSha"); payload["historyLimit"] = 101;
        Assert.NotNull(McpGatewayEndpoints.ValidateToolInput("history", JsonSerializer.SerializeToElement(payload, Json), tool.InputSchema));
        payload["historyLimit"] = 20; payload["historyOffset"] = -1;
        Assert.NotNull(McpGatewayEndpoints.ValidateToolInput("history", JsonSerializer.SerializeToElement(payload, Json), tool.InputSchema));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializedSdkReferencePassesGatewayValidation(bool includePatch)
    {
        var request = new ReviewGitMergeRequest(Guid.NewGuid(), 1, "reference") { IncludePatch = includePatch };
        Assert.Null(Validate(JsonSerializer.SerializeToElement(request, Json)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void SerializedSdkPagePassesGatewayValidation(string? target)
    {
        var request = Request() with { DiffRead = Request().DiffRead! with { TargetCommitSha = target } };
        Assert.Null(Validate(JsonSerializer.SerializeToElement(request, Json)));
    }

    [Theory]
    [InlineData("offset", "-1")]
    [InlineData("maximumCharacters", "0")]
    [InlineData("maximumCharacters", "12001")]
    [InlineData("publicationId", "\"invalid\"")]
    [InlineData("candidateCommitSha", "\"invalid\"")]
    [InlineData("targetCommitSha", "\"invalid\"")]
    [InlineData("patchDigest", "\"invalid\"")]
    [InlineData("unknownField", "true")]
    public void InvalidPageFieldsFailBeforeInvokingTheHandler(string field, string value)
    {
        var payload = JsonSerializer.SerializeToNode(Request(), Json)!.AsObject();
        payload["diffRead"]![field] = JsonNode.Parse(value);
        var result = Validate(JsonSerializer.SerializeToElement(payload, Json));
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("platform.capability.validation_failed", result.FailureCode);
    }

    [Fact]
    public void UnknownRootFieldsRemainRejected()
    {
        var payload = JsonSerializer.SerializeToNode(Request(), Json)!.AsObject();
        payload["installationId"] = Guid.NewGuid().ToString();
        Assert.NotNull(Validate(JsonSerializer.SerializeToElement(payload, Json)));
    }

    private static ReviewGitMergeRequest Request() => new(Guid.NewGuid(), 1, "page")
    {
        IncludePatch = false,
        DiffRead = new(Guid.NewGuid(), new string('a', 40), new string('b', 40), new string('c', 64), 0)
    };

    private static CapabilityResult? Validate(JsonElement payload)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { GitMergeCapabilities.Review }));
        return McpGatewayEndpoints.ValidateToolInput("review", payload, tool.InputSchema);
    }
}
