using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class PluginManifestProtocolTests
{
    [Fact]
    public void Protocol24ConsumerDoesNotNeedToOwnProviderAccounts()
    {
        var source = Consumer();
        Assert.Null(source["accounts"]);
        var envelope = Read(source);
        Assert.Equal("agent", envelope.Kind);
        Assert.Equal("com.example.workspace-assistant", envelope.Id);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("service")]
    [InlineData("connector")]
    public void Protocol24WithoutAccountManagementOrAgentDependenciesStillFails(string kind)
    {
        var source = Consumer(); source["kind"] = kind;
        if (kind == "agent") { source["dependencies"] = new JsonArray(); source["requires"] = new JsonArray(); }
        Assert.Throws<JsonException>(() => Read(source));
    }

    [Theory]
    [InlineData("undeclared-reference")]
    [InlineData("duplicate-dependency")]
    [InlineData("invalid-range")]
    [InlineData("missing-publisher")]
    [InlineData("raw-network")]
    [InlineData("provider-credential")]
    public void ConsumerProtocolSupportDoesNotBypassDependencyValidation(string invalid)
    {
        var source = Consumer(); var dependencies = (JsonArray)source["dependencies"]!;
        switch (invalid)
        {
            case "undeclared-reference": source["requires"]![0]!["dependency"] = "not-declared"; break;
            case "duplicate-dependency": dependencies.Add(dependencies[0]!.DeepClone()); break;
            case "invalid-range": dependencies[0]!["minimumVersion"] = "not-a-version"; break;
            case "missing-publisher": dependencies[0]!.AsObject().Remove("publisherId"); break;
            case "raw-network": source["webAccess"] = JsonNode.Parse("""{"mode":"allPublic","purpose":"Direct research"}"""); break;
            case "provider-credential": source["credentials"] = JsonNode.Parse("""[{"name":"API_KEY","type":"api-key","allowedOrigins":["https://api.example.com"]}]"""); break;
        }
        Assert.Throws<JsonException>(() => Read(source));
    }

    private static CSweet.Application.Setup.PluginManifestEnvelope Read(JsonNode source) =>
        new PluginManifestReader().Read(JsonSerializer.SerializeToUtf8Bytes(source), "csweet-plugin.json");

    private static JsonNode Consumer() => JsonNode.Parse("""
        {
          "manifestVersion": "2.0", "kind": "agent",
          "id": "com.example.workspace-assistant", "name": "Workspace assistant", "version": "1.0.0",
          "publisher": { "id": "com.example", "name": "Example" },
          "runtime": { "type": "dotnet-project", "projectPath": "src/Assistant/Assistant.csproj", "targetFramework": "net10.0" },
          "rolePolicy": { "profile": "individual-contributor.v1", "declaredRoleKeys": ["general-specialist"] },
          "protocol": { "minimumVersion": "2.4", "maximumVersion": "2.x" },
          "dependencies": [{ "id": "workspace", "pluginId": "com.example.workspace", "publisherId": "com.example", "minimumVersion": "1.0.0", "maximumVersionExclusive": "2.0.0" }],
          "requires": [{ "name": "example.workspace.read.v1", "dependency": "workspace", "scope": "organization", "purpose": "Read an explicitly authorized workspace" }],
          "catalog": { "role": { "key": "general-specialist", "name": "Workspace assistant" }, "license": { "spdxId": "MIT" } }
        }
        """)!;
}
