using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ConnectorResponseResourceTests
{
    [Fact]
    public void LegacyPreparedRequestsKeepTheirSerializedShape()
    {
        var operation = new PluginProviderOperationDeclaration { Effect = "read", Http = new()
            { Connection = "account", Endpoint = "https://api.example.com/items" } };
        var request = ConnectorRequestMaterializer.Prepare(operation, JsonSerializer.SerializeToElement(new { }), "confirmed");
        Assert.Null(request.ResponseResourcePointers);
        Assert.DoesNotContain("responseResourcePointers", JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("2.1", false)]
    [InlineData("2.2", true)]
    public async Task HostImporterRejectsOwnershipDeclarationsWithoutTheRequiredProtocol(string protocol, bool valid)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create(responseBinding: true);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Connector.PackageVersion!.ManifestJson, options)!;
        manifest = manifest with { ManifestVersion = "2.0", Protocol = manifest.Protocol with { MinimumVersion = protocol },
            Provides = [manifest.Provides[0] with { Description = "Read the confirmed account" }] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        if (valid) Assert.Equal("connector", new PluginManifestReader().Read(bytes, "csweet-plugin.json").Kind);
        else Assert.Contains("protocol 2.2", Assert.Throws<JsonException>(() => new PluginManifestReader().Read(bytes, "csweet-plugin.json")).Message);
    }

    private static ConnectorPreparedRequest Request(string pointer = "/items/*/owner") =>
        new("GET", "https://api.example.com/items", null, "account", "confirmed", "read", [], null, [],
            ResponseResourcePointers: [pointer]);

    [Theory]
    [InlineData("{\"items\":[{\"owner\":\"confirmed\"},{\"owner\":\"confirmed\"}]}")]
    [InlineData("{\"items\":[]}")]
    public void EveryOwnedRecordAndAnExistingEmptyCollectionAreAllowed(string json) =>
        ConnectorResponseResourceValidator.Validate(Encoding.UTF8.GetBytes(json), Request());

    [Theory]
    [InlineData("{\"items\":[{\"owner\":\"confirmed\"},{\"owner\":\"other-account\"}]}")]
    [InlineData("{\"items\":[{}]}")]
    [InlineData("{\"items\":[{\"owner\":null}]}")]
    [InlineData("{\"items\":[{\"owner\":[\"confirmed\"]}]}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{}")]
    public void MixedOwnersMissingPathsAndWrongShapesNeverReleaseData(string json)
    {
        var error = Assert.Throws<UnauthorizedAccessException>(() => ConnectorResponseResourceValidator.Validate(Encoding.UTF8.GetBytes(json), Request()));
        Assert.DoesNotContain("other-account", error.Message);
    }

    [Fact]
    public void DuplicateKeysCannotHideAWrongOwner() => Assert.Throws<InvalidOperationException>(() =>
        ConnectorResponseResourceValidator.Validate("{\"items\":[{\"owner\":\"other\",\"owner\":\"confirmed\"}]}"u8.ToArray(), Request()));

    [Fact]
    public void ScalarOwnershipIsExactAndCaseSensitive()
    {
        ConnectorResponseResourceValidator.Validate("{\"owner\":\"confirmed\"}"u8.ToArray(), Request("/owner"));
        Assert.Throws<UnauthorizedAccessException>(() => ConnectorResponseResourceValidator.Validate("{\"owner\":\"Confirmed\"}"u8.ToArray(), Request("/owner")));
    }

    [Fact]
    public void ResponseBindingsAreFrozenIntoTheCanonicalPlan()
    {
        var operation = new PluginProviderOperationDeclaration { Effect = "read", Http = new()
            { Connection = "account", Endpoint = "https://api.example.com/items", ResponseResourcePointers = ["/items/*/owner"] } };
        var request = ConnectorRequestMaterializer.Prepare(operation, JsonSerializer.SerializeToElement(new { }), "confirmed");
        Assert.Equal(new[] { "/items/*/owner" }, request.ResponseResourcePointers);
        Assert.NotEqual(ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(request)),
            ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(request with { ResponseResourcePointers = [] })));
    }
}
