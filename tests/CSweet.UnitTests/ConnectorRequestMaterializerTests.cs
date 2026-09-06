using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ConnectorRequestMaterializerTests
{
    [Fact]
    public void ConstantsAndBoundAccountCannotBeSubstitutedByInput()
    {
        var operation = Operation(new ConnectorHttpOperation
        {
            Endpoint = "https://api.example.com/items", Connection = "account", BoundResourceQuery = "owner",
            QueryConstants = new Dictionary<string, string> { ["part"] = "metadata" },
            QueryInputs = new Dictionary<string, string> { ["search"] = "/query" }
        });
        var result = ConnectorRequestMaterializer.Prepare(operation, Json("""{"query":"x&owner=attacker","owner":"attacker"}"""), "confirmed");
        Assert.Equal("https://api.example.com/items?owner=confirmed&part=metadata&search=x%26owner%3Dattacker", result.Url);
    }

    [Fact]
    public void BodyMappingCannotOverwriteConstant()
    {
        var operation = Operation(new ConnectorHttpOperation
        {
            BodyConstants = Json("""{"status":{"privacy":"private"}}"""),
            BodyInputs = new Dictionary<string, string> { ["/status/privacy"] = "/privacy" }
        });
        Assert.Throws<InvalidOperationException>(() => ConnectorRequestMaterializer.Prepare(operation,
            Json("""{"privacy":"public"}"""), "confirmed"));
    }

    [Fact]
    public void CanonicalHashIgnoresPropertyOrderButNotMutation()
    {
        var first = ConnectorRequestMaterializer.Hash(Json("""{"b":[2,1],"a":{"y":2,"x":1}}"""));
        Assert.Equal(first, ConnectorRequestMaterializer.Hash(Json("""{"a":{"x":1,"y":2},"b":[2,1]}""")));
        Assert.NotEqual(first, ConnectorRequestMaterializer.Hash(Json("""{"b":[1,2],"a":{"x":1,"y":2}}""")));
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejected() => Assert.Throws<InvalidOperationException>(() =>
        ConnectorRequestMaterializer.Hash(Json("""{"id":"allowed","id":"other"}""")));

    [Theory]
    [InlineData("https://files.example.com/video.mp4")]
    [InlineData("C:/secret.txt")]
    public void MediaMustBeOpaqueOrganizationAsset(string value) => Assert.Throws<InvalidOperationException>(() =>
        ConnectorRequestMaterializer.Prepare(Operation(new ConnectorHttpOperation { MediaInput = "/asset" }),
            JsonSerializer.SerializeToElement(new { asset = value }), "confirmed"));

    [Fact]
    public void OrdinaryOperationsCannotRunBeforeAccountConfirmation() => Assert.Throws<InvalidOperationException>(() =>
        ConnectorRequestMaterializer.Prepare(Operation(new()), Json("{}"), ""));

    private static PluginProviderOperationDeclaration Operation(ConnectorHttpOperation http) => new() { Http = http };
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
