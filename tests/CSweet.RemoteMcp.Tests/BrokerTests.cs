using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
namespace CSweet.RemoteMcp.Tests;

public sealed class BrokerTests
{
    private const string Key = "synthetic-key-never-a-real-credential";
    private readonly Repository repository = new();
    private readonly AccountScope scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private readonly Guid consumer = Guid.NewGuid();
    private AccountVault Vault() => new(repository, new EphemeralDataProtectionProvider());
    [Fact] public async Task ConnectRequiresGrantAndScopesAreIsolated()
    {
        var vault = Vault(); var account = await vault.ConnectAsync(scope, Key);
        Assert.DoesNotContain(Key, repository.Data[account.Id].Ciphertext);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, account, consumer, RealtyPolicy.Search, default));
        account = await vault.GrantAsync(scope, account, consumer);
        Assert.Equal(Key, await vault.RequireAsync(scope, account, consumer, RealtyPolicy.Search, default));
        foreach (var other in new[] { scope with { Owner = Guid.NewGuid() }, scope with { Business = Guid.NewGuid() }, scope with { ProviderInstallation = Guid.NewGuid() } })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(other, account, consumer, RealtyPolicy.Search, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, account, Guid.NewGuid(), RealtyPolicy.Search, default));
    }
    [Fact] public async Task RotationRevocationAndStaleQueuedWorkAreFenced()
    {
        var vault = Vault(); var first = await vault.ConnectAsync(scope, Key); var granted = await vault.GrantAsync(scope, first, consumer);
        var rotated = await vault.RotateAsync(scope, granted, Key + "-new");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, granted, consumer, RealtyPolicy.Search, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, rotated, consumer, RealtyPolicy.Search, default));
        var next = await vault.GrantAsync(scope, rotated, consumer); await vault.RevokeAsync(scope, next);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.RequireAsync(scope, next, consumer, RealtyPolicy.Search, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.GrantAsync(scope, first, consumer));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task OnlyReviewedStatelessReadsAreReleased(bool sse)
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer);
        var fake = new Fake { Sse = sse };
        var result = await new RemoteMcpBroker(vault, () => fake).ReadAsync(scope, account, consumer, RealtyPolicy.Search, JsonSerializer.SerializeToElement(new { state = "TX", limit = 10 }));
        Assert.Equal(JsonValueKind.Array, result.ValueKind); Assert.Equal(4, fake.Calls);
    }
    [Theory] [InlineData("unauthorized")] [InlineData("redirect")] [InlineData("echo")] [InlineData("encoded-echo")] [InlineData("oversize")] [InlineData("session")] [InlineData("extra-tool")] [InlineData("bad-id")] [InlineData("bad-json")] [InlineData("sse-multiple")] [InlineData("sse-replay")] [InlineData("bad-content-type")]
    public async Task UnsafeResponsesAreWithheld(string mode)
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer);
        var error = await Assert.ThrowsAsync<RemoteMcpFailureException>(() => new RemoteMcpBroker(vault, () => new Fake { Mode = mode }).ReadAsync(scope, account, consumer, RealtyPolicy.Search, JsonSerializer.SerializeToElement(new { })));
        Assert.DoesNotContain(Key, error.ToString());
    }
    [Fact] public async Task LookupReleasesOnlyReviewedLeadProjection()
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer);
        var result = await new RemoteMcpBroker(vault, () => new Fake()).ReadAsync(scope, account, consumer, RealtyPolicy.Lookup,
            JsonSerializer.SerializeToElement(new { id = "00000000-0000-0000-0000-000000000001" }));
        Assert.Equal("Fixture address", result.GetProperty("address").GetString());
        Assert.Equal(11, result.EnumerateObject().Count());
    }
    [Fact] public async Task RevocationDuringCallWithholdsResult()
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer);
        var fake = new Fake { BeforeResult = async () => { await vault.RevokeAsync(scope, account); } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RemoteMcpBroker(vault, () => fake).ReadAsync(scope, account, consumer, RealtyPolicy.Search, JsonSerializer.SerializeToElement(new { })));
    }
    [Theory] [InlineData("numeric-address")] [InlineData("string-score")] [InlineData("overflow-score")]
    public async Task InvalidLeadFieldTypesAreWithheldBeforeLeavingBroker(string mode)
    {
        var vault = Vault(); var account = await vault.GrantAsync(scope, await vault.ConnectAsync(scope, Key), consumer);
        await Assert.ThrowsAsync<RemoteMcpFailureException>(() => new RemoteMcpBroker(vault, () => new Fake { Mode = mode }).ReadAsync(
            scope, account, consumer, RealtyPolicy.Lookup, JsonSerializer.SerializeToElement(new { id = "00000000-0000-0000-0000-000000000001" })));
    }
    [Theory] [InlineData("{\"url\":\"http://localhost\"}")] [InlineData("{\"headers\":{\"X-Api-Key\":\"bad\"}}")] [InlineData("{\"limit\":101}")] [InlineData("{\"limit\":\"bad\"}")] [InlineData("{\"limit\":1,\"limit\":2}")]
    public void InputCannotControlTransport(string json) => Assert.Throws<ArgumentException>(() => RealtyPolicy.ValidateInput(RealtyPolicy.Search, JsonDocument.Parse(json).RootElement));
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(RealtyPolicy.Endpoint, request.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Key, request.Headers.GetValues("X-Api-Key").Single()); Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = body.RootElement.GetProperty("method").GetString();
            if (Mode is "unauthorized" or "redirect") return new(Mode == "redirect" ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.Unauthorized) { Content = new StringContent(Key) };
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            var leadJson = method == "tools/call" && body.RootElement.GetProperty("params").GetProperty("name").GetString() == "get_inheritance_lead_by_id"
                ? JsonSerializer.Serialize(new { inheritanceLeadId = "00000000-0000-0000-0000-000000000001", propertyId = "00000000-0000-0000-0000-000000000002",
                    address = "Fixture address", ownerName = "Fixture owner", status = "Verified", score = 90, deceasedId = "00000000-0000-0000-0000-000000000003",
                    deceasedName = "Fixture person", cityName = "Fixture city", countyName = "Fixture county", stateAbbreviation = "TX" }) : "[]";
            if (Mode == "numeric-address") leadJson = leadJson.Replace("\"address\":\"Fixture address\"", "\"address\":123");
            if (Mode == "string-score") leadJson = leadJson.Replace("\"score\":90", "\"score\":\"90\"");
            if (Mode == "overflow-score") leadJson = leadJson.Replace("\"score\":90", "\"score\":1e999");
            object result = method switch
            {
                "initialize" => new { protocolVersion = RealtyPolicy.Protocol },
                "tools/list" => new { tools = Tools(Mode == "extra-tool") },
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
        private static object[] Tools(bool extra)
        {
            object[] tools = [new { name = "search_inheritance_leads", inputSchema = new { type = "object", properties = new { state = new { type = "string" }, county = new { type = "string" }, city = new { type = "string" }, status = new { type = "string" }, minScore = new { type = "number" }, limit = new { type = "integer" } } } },
                new { name = "get_inheritance_lead_by_id", inputSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } } }];
            return extra ? [..tools, new { name = "delete_all" }] : tools;
        }
    }
}
