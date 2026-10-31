using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
namespace CSweet.RemoteMcp.Tests;

public sealed class BrokerTests
{
    private const string Key = "synthetic-key-never-a-real-credential";
    private const string Search = "realtyleads.inheritance.search.v1";
    private const string Lookup = "realtyleads.inheritance.lookup.v1";
    private static readonly string[] Capabilities = [Search, Lookup];
    internal static string ManifestJson => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "realtyleads-plugin.json"));
    private static readonly ReviewedRemoteMcpProfile Profile = ReviewedRemoteMcpProfile.FromManifestJson(ManifestJson);
    private readonly Repository repository = new();
    private readonly AccountScope scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private readonly Guid consumer = Guid.NewGuid();
    private AccountVault Vault() => new(repository, new EphemeralDataProtectionProvider());
    [Fact] public async Task ConnectRequiresGrantAndScopesAreIsolated()
    {
        var vault = Vault(); var account = await vault.ConnectAsync(scope, Key);
        Assert.DoesNotContain(Key, repository.Data[account.Id].Ciphertext);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, account, consumer, Search, default));
        account = await vault.GrantAsync(scope, account, consumer, Capabilities);
        Assert.Equal(Key, await vault.RequireAsync(scope, account, consumer, Search, default));
        foreach (var other in new[] { scope with { Owner = Guid.NewGuid() }, scope with { Business = Guid.NewGuid() }, scope with { ProviderInstallation = Guid.NewGuid() } })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(other, account, consumer, Search, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, account, Guid.NewGuid(), Search, default));
    }
    [Fact] public async Task RotationRevocationAndStaleQueuedWorkAreFenced()
    {
        var vault = Vault(); var first = await vault.ConnectAsync(scope, Key); var granted = await vault.GrantAsync(scope, first, consumer, Capabilities);
        var rotated = await vault.RotateAsync(scope, granted, Key + "-new");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, granted, consumer, Search, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, rotated, consumer, Search, default));
        var next = await vault.GrantAsync(scope, rotated, consumer, Capabilities); await vault.RevokeAsync(scope, next);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, next, consumer, Search, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.GrantAsync(scope, first, consumer, Capabilities));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task OnlyReviewedStatelessReadsAreReleased(bool sse)
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var fake = new Fake { Sse = sse };
        var result = await new RemoteMcpBroker(vault, Profile, () => fake).ReadAsync(scope, account, consumer, Search, JsonSerializer.SerializeToElement(new { state = "TX", limit = 10 }));
        Assert.Equal(JsonValueKind.Array, result.ValueKind); Assert.Equal(4, fake.Calls);
    }
    [Theory] [InlineData("unauthorized")] [InlineData("redirect")] [InlineData("echo")] [InlineData("encoded-echo")] [InlineData("oversize")] [InlineData("session")] [InlineData("missing-tool")] [InlineData("retyped-tool")] [InlineData("duplicate-tool")] [InlineData("paged-tools")] [InlineData("bad-id")] [InlineData("bad-json")] [InlineData("sse-multiple")] [InlineData("sse-replay")] [InlineData("bad-content-type")]
    public async Task UnsafeResponsesAreWithheld(string mode)
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var error = await Assert.ThrowsAsync<RemoteMcpFailureException>(() => new RemoteMcpBroker(vault, Profile, () => new Fake { Mode = mode }).ReadAsync(scope, account, consumer, Search, JsonSerializer.SerializeToElement(new { })));
        Assert.DoesNotContain(Key, error.ToString());
    }
    [Fact] public async Task LookupReleasesOnlyReviewedLeadProjection()
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var result = await new RemoteMcpBroker(vault, Profile, () => new Fake()).ReadAsync(scope, account, consumer, Lookup,
            JsonSerializer.SerializeToElement(new { id = "00000000-0000-0000-0000-000000000001" }));
        Assert.Equal("Fixture address", result.GetProperty("address").GetString());
        Assert.Equal(11, result.EnumerateObject().Count());
    }
    [Fact] public async Task RevocationDuringCallWithholdsResult()
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var fake = new Fake { BeforeResult = async () => { await vault.RevokeAsync(scope, account); } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RemoteMcpBroker(vault, Profile, () => fake).ReadAsync(scope, account, consumer, Search, JsonSerializer.SerializeToElement(new { })));
    }
    [Theory] [InlineData("numeric-address")] [InlineData("string-score")] [InlineData("overflow-score")]
    public async Task InvalidLeadFieldTypesAreWithheldBeforeLeavingBroker(string mode)
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        await Assert.ThrowsAsync<RemoteMcpFailureException>(() => new RemoteMcpBroker(vault, Profile, () => new Fake { Mode = mode }).ReadAsync(
            scope, account, consumer, Lookup, JsonSerializer.SerializeToElement(new { id = "00000000-0000-0000-0000-000000000001" })));
    }
    [Theory] [InlineData("{\"url\":\"http://localhost\"}")] [InlineData("{\"headers\":{\"X-Api-Key\":\"bad\"}}")] [InlineData("{\"limit\":101}")] [InlineData("{\"limit\":\"bad\"}")] [InlineData("{\"limit\":1,\"limit\":2}")]
    public void InputCannotControlTransport(string json) => Assert.Throws<ArgumentException>(() => Profile.ValidateInput(Search, JsonDocument.Parse(json).RootElement));
    [Fact] public void UndeclaredCapabilitiesAreRefused() =>
        Assert.Throws<UnauthorizedAccessException>(() => Profile.ValidateInput("realtyleads.inheritance.delete.v1", JsonDocument.Parse("{}").RootElement));
    [Fact] public async Task UnmappedAdvertisedToolsAreIgnoredAndNeverCalled()
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var fake = new Fake { Mode = "extra-tool" };
        await new RemoteMcpBroker(vault, Profile, () => fake).ReadAsync(scope, account, consumer, Search, JsonSerializer.SerializeToElement(new { limit = 1 }));
        Assert.Equal("search_inheritance_leads", Assert.Single(fake.CalledTools));
    }
    [Fact] public void RetiredClosedProfileCannotBuildAPolicy()
    {
        var retired = JsonNode.Parse(ManifestJson)!.AsObject();
        retired["protocol"]!["minimumVersion"] = "2.5";
        retired["reviewedRemoteMcp"]!.AsObject().Remove("tools");
        Assert.Throws<InvalidOperationException>(() => ReviewedRemoteMcpProfile.FromManifestJson(retired.ToJsonString()));
        Assert.Throws<InvalidOperationException>(() => ReviewedRemoteMcpProfile.FromManifestJson("{not json"));
    }
    [Theory]
    [InlineData("\"Endpoint\": \"https://evil.example/mcp\",")]
    [InlineData("\"endpoint\": \"https://evil.example/mcp\",")]
    public void CaseVariantOrDuplicateDeclarationsAreRejected(string injected)
    {
        var ambiguous = ManifestJson.Replace("\"endpoint\": \"https://realtyleads.ai/mcp\",", "\"endpoint\": \"https://realtyleads.ai/mcp\", " + injected);
        Assert.NotEqual(ManifestJson, ambiguous);
        Assert.Throws<InvalidOperationException>(() => ReviewedRemoteMcpProfile.FromManifestJson(ambiguous));
    }
    [Theory] [InlineData("\"provides\": null")] [InlineData("\"events\": {\"subscribes\": null}")]
    public void NullSectionsAreDeniedNotCrashed(string replacement)
    {
        var node = JsonNode.Parse(ManifestJson)!.AsObject();
        var name = replacement.StartsWith("\"provides", StringComparison.Ordinal) ? "provides" : "events";
        node[name] = JsonNode.Parse("{" + replacement + "}")![name]?.DeepClone();
        Assert.Throws<InvalidOperationException>(() => ReviewedRemoteMcpProfile.FromManifestJson(node.ToJsonString()));
    }
    [Fact] public async Task AnotherPublishersDeclarationDrivesEndpointHeaderAndToolNames()
    {
        var other = JsonNode.Parse(ManifestJson)!.AsObject();
        other["id"] = "com.acme.plugin.leads";
        other["name"] = "Acme Leads";
        other["publisher"] = new JsonObject { ["id"] = "com.acme", ["name"] = "Acme" };
        var remote = other["reviewedRemoteMcp"]!;
        remote["endpoint"] = "https://api.acme.example/v1/mcp";
        remote["header"] = "X-Acme-Key";
        remote["tools"]![0]!["remoteTool"] = "acme_search";
        remote["tools"]![1]!["remoteTool"] = "acme_get";
        var profile = ReviewedRemoteMcpProfile.FromManifestJson(other.ToJsonString());
        Assert.Equal("api.acme.example", profile.Host); Assert.Equal("Acme Leads", profile.ProviderName);
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var fake = new Fake { Endpoint = "https://api.acme.example/v1/mcp", Header = "X-Acme-Key", SearchTool = "acme_search", LookupTool = "acme_get" };
        var result = await new RemoteMcpBroker(vault, profile, () => fake).ReadAsync(scope, account, consumer, Lookup,
            JsonSerializer.SerializeToElement(new { id = "00000000-0000-0000-0000-000000000001" }));
        Assert.Equal("Fixture address", result.GetProperty("address").GetString());
        Assert.Equal("acme_get", Assert.Single(fake.CalledTools));
    }
    [Fact] public async Task BearerAuthenticationUsesTheAuthorizationHeaderOnly()
    {
        var bearer = JsonNode.Parse(ManifestJson)!.AsObject();
        bearer["reviewedRemoteMcp"]!["authentication"] = "bearer";
        bearer["reviewedRemoteMcp"]!["header"] = "Authorization";
        var profile = ReviewedRemoteMcpProfile.FromManifestJson(bearer.ToJsonString());
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, Capabilities);
        var fake = new Fake { Header = null };
        await new RemoteMcpBroker(vault, profile, () => fake).ReadAsync(scope, account, consumer, Search, JsonSerializer.SerializeToElement(new { limit = 1 }));
        Assert.Equal(4, fake.Calls);
    }
    [Fact] public async Task GrantsCoverOnlyTheSuppliedCapabilities()
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer, [Lookup]);
        Assert.Equal(Key, await vault.RequireAsync(scope, account, consumer, Lookup, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, account, consumer, Search, default));
        var fresh = await vault.ConnectAsync(scope, Key);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.GrantAsync(scope, fresh, consumer, []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.GrantAsync(scope, fresh, consumer, [Search, Search]));
    }
    [Theory] [InlineData("")] [InlineData("short")] [InlineData("synthetic-key\r\nInjected:yes")]
    public async Task InvalidKeysAreRejectedBeforeStorage(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Vault().ConnectAsync(scope, key));
        Assert.Empty(repository.Data);
    }
    private sealed class Repository : IAccountRepository
    {
        internal readonly ConcurrentDictionary<Guid, ProtectedAccount> Data = new();
        public Task<ProtectedAccount?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult(Data.GetValueOrDefault(id));
        public Task<bool> CompareExchangeAsync(Guid id, long? expected, ProtectedAccount next, CancellationToken ct)
        {
            lock (Data) { if (Data.GetValueOrDefault(id)?.Revision != expected) return Task.FromResult(false); Data[id] = next; return Task.FromResult(true); }
        }
    }
    private sealed class Fake : HttpMessageHandler
    {
        public string Mode = ""; public bool Sse; public int Calls; public Func<Task>? BeforeResult;
        public string Endpoint = "https://realtyleads.ai/mcp"; public string? Header = "X-Api-Key";
        public string SearchTool = "search_inheritance_leads"; public string LookupTool = "get_inheritance_lead_by_id";
        public readonly List<string> CalledTools = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(Endpoint, request.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, request.Method);
            if (Header is null)
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(Key, request.Headers.Authorization.Parameter);
                Assert.False(request.Headers.Contains("X-Api-Key"));
            }
            else { Assert.Equal(Key, request.Headers.GetValues(Header).Single()); Assert.Null(request.Headers.Authorization); }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = body.RootElement.GetProperty("method").GetString();
            if (Mode is "unauthorized" or "redirect") return new(Mode == "redirect" ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.Unauthorized) { Content = new StringContent(Key) };
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            if (method == "tools/call") CalledTools.Add(body.RootElement.GetProperty("params").GetProperty("name").GetString()!);
            var leadJson = method == "tools/call" && body.RootElement.GetProperty("params").GetProperty("name").GetString() == LookupTool
                ? JsonSerializer.Serialize(new { inheritanceLeadId = "00000000-0000-0000-0000-000000000001", propertyId = "00000000-0000-0000-0000-000000000002",
                    address = "Fixture address", ownerName = "Fixture owner", status = "Verified", score = 90, deceasedId = "00000000-0000-0000-0000-000000000003",
                    deceasedName = "Fixture person", cityName = "Fixture city", countyName = "Fixture county", stateAbbreviation = "TX" }) : "[]";
            if (Mode == "numeric-address") leadJson = leadJson.Replace("\"address\":\"Fixture address\"", "\"address\":123");
            if (Mode == "string-score") leadJson = leadJson.Replace("\"score\":90", "\"score\":\"90\"");
            if (Mode == "overflow-score") leadJson = leadJson.Replace("\"score\":90", "\"score\":1e999");
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-03-26" },
                "tools/list" => Mode == "paged-tools" ? new { tools = Tools(""), nextCursor = "2" } : (object)new { tools = Tools(Mode) },
                _ => new { content = new[] { new { type = "text", text = leadJson } } }
            };
            if (method == "tools/call" && BeforeResult is not null) await BeforeResult();
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Mode == "bad-id" ? 99 : id, result });
            if (Mode == "echo") json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = Key });
            if (Mode == "encoded-echo") json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = Convert.ToBase64String(Encoding.UTF8.GetBytes(Key)) });
            if (Mode == "oversize") json = new string('x', 1_048_577);
            if (Mode == "bad-json") json = "{bad";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse ? $"event: message\ndata: {json}\n\n" : json, Encoding.UTF8, Sse ? "text/event-stream" : "application/json") };
            if (Mode == "sse-multiple") response.Content = new StringContent($"data: {json}\n\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream");
            if (Mode == "sse-replay") response.Content = new StringContent($"id: 123\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream");
            if (Mode == "bad-content-type") response.Content = new StringContent(json, Encoding.UTF8, "text/html");
            if (Mode == "session") response.Headers.Add("Mcp-Session-Id", "unexpected-stateful-session");
            return response;
        }
        private object[] Tools(string mode)
        {
            object search = new { name = mode == "missing-tool" ? "renamed_search" : SearchTool, inputSchema = new { type = "object", properties = new { state = new { type = mode == "retyped-tool" ? "integer" : "string" }, county = new { type = "string" }, city = new { type = "string" }, status = new { type = "string" }, minScore = new { type = "number" }, limit = new { type = "integer" } } } };
            object lookup = new { name = LookupTool, inputSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } } };
            object[] tools = [search, lookup];
            return mode switch
            {
                "extra-tool" => [.. tools, new { name = "delete_all", inputSchema = new { type = "object" } }],
                "duplicate-tool" => [.. tools, search],
                _ => tools
            };
        }
    }
}
