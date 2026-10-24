using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ConnectorResourceDiscoveryTests
{
    private static readonly PluginAccountManagement Declaration = new()
    {
        Connection = "account", DiscoveryCapability = "example.api.discover.v1", PageTokenInput = "pageToken", MaximumResources = 100,
        ResourceProjection = new() { ItemsPointer = "/items", IdPointer = "/id", NamePointer = "/name", NextPageTokenPointer = "/next" }
    };
    private static readonly PluginProviderOperationDeclaration Operation = new()
    {
        Capability = "example.api.discover.v1", Effect = "read",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false,"properties":{"pageToken":{"type":"string","maxLength":2048}}}"""),
        OutputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}"""),
        Http = new() { Connection = "account", Bootstrap = true, Method = "GET" }
    };

    [Fact]
    public async Task CollectsEveryPageAndToleratesIdenticalOverlap()
    {
        var calls = 0;
        var result = await ConnectorResourceDiscovery.CollectAsync(Declaration, Operation, (input, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Empty(input.EnumerateObject());
                return Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""{"items":[{"id":"one","name":"First"}],"next":"page-two"}"""));
            }
            Assert.Equal("page-two", input.GetProperty("pageToken").GetString());
            return Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""{"items":[{"id":"one","name":"First"},{"id":"two","name":"Second"}]}"""));
        }, default);
        Assert.Equal(2, calls); Assert.Equal(new[] { "one", "two" }, result.Select(x => x.Id));
    }

    [Theory]
    [InlineData("{\"items\":[],\"next\":4}")]
    [InlineData("{\"items\":[],\"next\":\"again\"}")]
    [InlineData("{\"items\":[{\"id\":\"one\",\"name\":\"Conflicting\"}]}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":[],\"items\":[]}")]
    public async Task InvalidContinuationConflictingIdentityOrMalformedPageCannotProducePartialSuccess(string secondPage)
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectorResourceDiscovery.CollectAsync(Declaration, Operation, (_, _) =>
            Task.FromResult(JsonSerializer.Deserialize<JsonElement>(++calls == 1
                ? """{"items":[{"id":"one","name":"First"}],"next":"again"}""" : secondPage)), default));
    }

    [Fact]
    public async Task ResourceBudgetIsEnforcedAcrossPages()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectorResourceDiscovery.CollectAsync(Declaration with { MaximumResources = 1 }, Operation,
            (_, _) => Task.FromResult(JsonSerializer.Deserialize<JsonElement>(++calls == 1
                ? """{"items":[{"id":"one","name":"First"}],"next":"second"}"""
                : """{"items":[{"id":"two","name":"Second"}]}""")), default));
    }

    [Fact]
    public async Task PageFailureOrCancellationDoesNotReturnEarlierResults()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => ConnectorResourceDiscovery.CollectAsync(Declaration, Operation, (_, _) =>
            ++calls == 1 ? Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""{"items":[],"next":"second"}"""))
                : throw new HttpRequestException("provider unavailable"), default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectorResourceDiscovery.CollectAsync(Declaration, Operation,
            (_, _) => throw new Exception("must not be called"), cancellation.Token));
    }

    [Fact]
    public async Task MutationCannotMasqueradeAsDiscovery() => await Assert.ThrowsAsync<InvalidOperationException>(() =>
        ConnectorResourceDiscovery.CollectAsync(Declaration, Operation with { Effect = "write" }, (_, _) => throw new Exception("must not be called"), default));
}
