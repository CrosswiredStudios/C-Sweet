using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentOperatingStateKeyContractTests
{
    public static IEnumerable<object[]> Keys()
    {
        yield return [new string('a', 160), true];
        yield return [new string('a', 161), false];
        yield return ["game-project-identity:v1:" + new string('a', 64) + ":attempt:" + new string('b', 64), false];
        yield return ["letters-._/:123", true];
        yield return ["has space", false];
        yield return ["unicode-é", false];
        yield return ["line\n", false];
        yield return ["", false];
        yield return [AgentOperatingStateKey.Create("game-project-identity-attempt:v1", new { source = new string('a', 64), provider = Guid.Empty, modelName = new string('x', 1000) }), true];
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task CatalogHandlerAndSdkUseTheSameKeyContract(string key, bool accepted)
    {
        var read = new AgentOperatingStateReadRequest(key);
        var write = new AgentOperatingStateWriteRequest(key, "test.v1", 1, "Active", new Dictionary<string, string>(), [],
            "fingerprint", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new { ready = true }), null, "once");
        var catalog = new McpToolCatalog([]);
        foreach (var (capability, request, sharedSchema) in new[] {
            (PlatformCapabilities.AgentOperatingStateRead, (object)read, AgentOperatingStateValidation.ReadInputSchema),
            (PlatformCapabilities.AgentOperatingStateWrite, (object)write, AgentOperatingStateValidation.WriteInputSchema)
        })
        {
            var tool = Assert.Single(catalog.List(new HashSet<string> { capability }));
            Assert.Equal(sharedSchema.GetRawText(), tool.InputSchema.GetRawText());
            var payload = JsonSerializer.SerializeToElement(request, Json);
            var schemaError = Record.Exception(() => JsonSchemaValidator.Validate(payload, tool.InputSchema));
            Assert.Equal(accepted, schemaError is null);
            var sdkError = Record.Exception(() => AgentOperatingStateValidation.ValidateInput(capability, payload));
            Assert.Equal(accepted, sdkError is null);

            await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
            var session = new AgentSession("session", "test-agent", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { capability }, 1));
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, new RequestCapability {
                RequestId = Guid.NewGuid().ToString(), Capability = capability,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(request, Json))
            }, default)) results.Add(result);
            Assert.Equal(accepted, Assert.Single(results).Succeeded);
            Assert.Equal(accepted && capability == PlatformCapabilities.AgentOperatingStateWrite ? 1 : 0, db.PluginOperationalStates.Count());
        }
    }

    [Fact]
    public async Task BuiltAttemptKeyCanBeWrittenAndReadThroughRealCatalogAndHandler()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
        var session = new AgentSession("session", "naomi", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), 1));
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                async (request, token) => (await Invoke(PlatformCapabilities.AgentOperatingStateWrite, request, token)).Deserialize<AgentOperatingStateResponse>(Json)!)
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                async (request, token) => (await Invoke(PlatformCapabilities.AgentOperatingStateRead, request, token)).Deserialize<AgentOperatingStateReadResponse>(Json)!);
        var key = AgentOperatingStateKey.Create("game-project-identity-attempt:v1", new { source = new string('a', 64), provider = Guid.NewGuid(), modelName = "unsloth/flint-GGUF" });
        var request = new AgentOperatingStateWriteRequest(key, "identity.v1", 1, "Active", new Dictionary<string, string>(), [],
            "fingerprint", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new { title = "RESONANCE" }), null, key);
        var client = runtime.CreateContext().Platform;
        var first = await client.WriteOperatingStateAsync(request);
        var replay = await client.WriteOperatingStateAsync(request);
        var saved = await client.ReadOperatingStateAsync(new AgentOperatingStateReadRequest(key));
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Id, saved!.Id);
        Assert.Single(db.PluginOperationalStates);

        async Task<JsonElement> Invoke(string capability, object payload, CancellationToken token)
        {
            var json = JsonSerializer.SerializeToElement(payload, Json);
            var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
            JsonSchemaValidator.Validate(json, tool.InputSchema);
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, new RequestCapability {
                RequestId = Guid.NewGuid().ToString(), Capability = capability, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, Json))
            }, token)) results.Add(result);
            var response = Assert.Single(results);
            Assert.True(response.Succeeded, response.Error);
            return JsonSerializer.Deserialize<JsonElement>(response.Payload.ToByteArray());
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
