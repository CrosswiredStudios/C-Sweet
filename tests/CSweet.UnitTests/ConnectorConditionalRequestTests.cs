using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ConnectorConditionalRequestTests
{
    [Theory]
    [InlineData("2.0", true)]
    [InlineData("2.1", true)]
    [InlineData("2.2", true)]
    [InlineData("2.3", true)]
    [InlineData("2.4", false)]
    public void ManifestReaderAcceptsOnlyImplementedProtocolVersions(string minimum, bool accepted)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { manifestVersion = "2.0", kind = "service", id = "com.example.service",
            name = "Example", version = "0.1.0", protocol = new { minimumVersion = minimum, maximumVersion = "2.x" } });
        if (accepted) Assert.Equal("com.example.service", new PluginManifestReader().Read(bytes, "csweet-plugin.json").Id);
        else Assert.Throws<JsonException>(() => new PluginManifestReader().Read(bytes, "csweet-plugin.json"));
    }

    private static ConnectorPreparedRequest Request(string tag = "\"v1\"") =>
        new("PUT", "https://api.example.com/items", "{}", "account", "confirmed", "write", [], null, [], IfMatch: tag);

    [Fact]
    public void ExactHeaderIsAppliedWithoutOtherHeaders()
    {
        var request = Request(); using var message = new HttpRequestMessage(HttpMethod.Put, request.Url);
        ConnectorHttpTransport.ApplyPrecondition(message, request);
        Assert.Equal("\"v1\"", Assert.Single(message.Headers.IfMatch).ToString());
        Assert.Single(message.Headers);
    }

    [Theory]
    [InlineData("GET", "write", null)]
    [InlineData("POST", "write", null)]
    [InlineData("PUT", "read", null)]
    [InlineData("PUT", "write", "asset")]
    public void ConditionCannotEscapeItsMutationBoundary(string method, string effect, string? media)
    {
        var request = Request() with { Method = method, Effect = effect, MediaAssetId = media };
        using var message = new HttpRequestMessage(new HttpMethod(method), request.Url);
        Assert.Throws<InvalidOperationException>(() => ConnectorHttpTransport.ApplyPrecondition(message, request));
        Assert.Empty(message.Headers);
    }

    [Theory]
    [InlineData("PATCH", "https://api.example.com/items")]
    [InlineData("PUT", "https://api.example.com/other")]
    public void ConditionRequiresExactOutgoingRequest(string method, string url)
    {
        using var message = new HttpRequestMessage(new HttpMethod(method), url);
        Assert.Throws<InvalidOperationException>(() => ConnectorHttpTransport.ApplyPrecondition(message, Request()));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"v1\"")]
    [InlineData("\"v1\",\"v2\"")]
    [InlineData("\"v1\r\nInjected: x\"")]
    public void MaterializationRejectsUnsafeValuesBeforeApproval(string tag)
    {
        var operation = new PluginProviderOperationDeclaration { Effect = "write", Http = new() {
            Method = "PUT", Endpoint = "https://api.example.com/items", Connection = "account", IfMatchInput = "/etag" } };
        Assert.Throws<ArgumentException>(() => ConnectorRequestMaterializer.Prepare(operation, JsonSerializer.SerializeToElement(new { etag = tag }), "confirmed"));
    }

    [Fact]
    public void VersionChangesAlterHashButAbsentConditionPreservesLegacySerialization()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var first = JsonSerializer.SerializeToElement(Request(), options);
        var second = JsonSerializer.SerializeToElement(Request("\"v2\""), options);
        Assert.NotEqual(ConnectorRequestMaterializer.Hash(first), ConnectorRequestMaterializer.Hash(second));
        Assert.False(JsonSerializer.SerializeToElement(Request() with { IfMatch = null }, options).TryGetProperty("ifMatch", out _));
    }
}
