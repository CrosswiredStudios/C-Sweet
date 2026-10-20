using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class ResearchAndCheckpointSchemaTests
{
    [Theory]
    [InlineData(ResearchCapabilities.Search, "{\"query\":\"official Node documentation\"}")]
    [InlineData(ResearchCapabilities.Read, "{\"url\":\"https://nodejs.org/api/test.html\"}")]
    [InlineData(ExecutionCheckpointCapabilities.Read, "{\"sessionKey\":\"task-1\",\"engineVersion\":\"test-1\"}")]
    [InlineData(ExecutionCheckpointCapabilities.Read, "{\"sessionKey\":\"task-1\",\"engineVersion\":\"test-1\",\"mode\":\"chunk\",\"revision\":1,\"blob\":\"engine\",\"offset\":0}")]
    [InlineData(ExecutionCheckpointCapabilities.Write, "{\"mode\":\"stage\",\"sessionKey\":\"task-1\",\"engineVersion\":\"test-1\",\"digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"offset\":0,\"totalLength\":1,\"data\":\"AA==\"}")]
    [InlineData(ExecutionCheckpointCapabilities.Write, "{\"mode\":\"commit\",\"request\":{},\"engineDigest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData(ExecutionCheckpointCapabilities.Write, "{\"sessionKey\":\"task-1\",\"engineVersion\":\"test-1\",\"expectedRevision\":0,\"eventCursor\":1,\"engineArchive\":\"AA==\",\"idempotencyKey\":\"event-1\"}")]
    public void PublishedToolsLoadAndAcceptTypedRequestShapes(string capability, string payload)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
        JsonSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>(payload), tool.InputSchema);
    }

    [Theory]
    [InlineData(ExecutionCheckpointCapabilities.Read, "{\"sessionKey\":\"task-1\",\"engineVersion\":\"test-1\",\"mode\":\"chunk\"}")]
    [InlineData(ExecutionCheckpointCapabilities.Write, "{\"sessionKey\":\"task-1\",\"engineVersion\":\"test-1\",\"expectedRevision\":0,\"eventCursor\":1,\"engineArchive\":\"AA==\",\"idempotencyKey\":\"event-1\",\"mode\":\"stage\"}")]
    public void IncompleteChunkModesCannotMatchTheInlineShape(string capability, string payload)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
        Assert.Throws<InvalidOperationException>(() => JsonSchemaValidator.Validate(
            JsonSerializer.Deserialize<JsonElement>(payload), tool.InputSchema));
    }
}
