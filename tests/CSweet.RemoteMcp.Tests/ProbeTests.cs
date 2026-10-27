using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.RemoteMcp.Probe;
using Xunit;
namespace CSweet.RemoteMcp.Tests;

public sealed class ProbeTests
{
    private const string Key = "synthetic-probe-key-only-0001";
    [Theory]
    [InlineData("one", "passed", 1, true, 8)]
    [InlineData("empty", "passed", 0, false, 4)]
    [InlineData("unauthorized", "failed", 0, false, 1)]
    [InlineData("echo", "failed", 0, false, 1)]
    [InlineData("cancel", "failed", 0, false, 1)]
    [InlineData("too-many", "failed", 0, false, 4)]
    [InlineData("different-id", "failed", 1, false, 8)]
    public async Task BoundedProbeUsesRealBrokerAndAlwaysRevokes(string mode, string status, int count, bool lookup, int requests)
    {
        var state = new State(mode);
        var outcome = await new ProbeRunner().RunAsync(Key, default, vault => new RemoteMcpBroker(vault, () => new Fake(state)));
        Assert.Equal(status, outcome.Status); Assert.Equal(count, outcome.SearchCount); Assert.Equal(lookup, outcome.LookupSucceeded);
        Assert.True(outcome.RevocationVerified); Assert.Equal(requests, state.Requests);
        Assert.DoesNotContain(Key, outcome.ToString()); Assert.DoesNotContain("Fixture private address", outcome.ToString());
    }
    [Fact] public async Task InvalidKeyNeverOpensTransport()
    {
        var state = new State("one");
        var result = await new ProbeRunner().RunAsync("bad", default, vault => new RemoteMcpBroker(vault, () => new Fake(state)));
        Assert.Equal("failed", result.Status); Assert.Equal(0, state.Requests);
    }
    [Theory]
    [InlineData("unauthorized", "AuthenticationRejected", "Initialize", 401)]
    [InlineData("forbidden", "AccessPolicyRejected", "Initialize", 403)]
    [InlineData("dns", "DnsFailure", "Initialize", null)]
    [InlineData("tls", "TlsFailure", "Initialize", null)]
    [InlineData("connect", "ConnectionFailure", "Initialize", null)]
    [InlineData("cancel", "Timeout", "Initialize", null)]
    [InlineData("schema", "ToolSchemaRejected", "ToolsList", 200)]
    [InlineData("protocol", "ProtocolMismatch", "Initialize", 200)]
    public async Task DiagnosticsAreActionableAndNeverContainRemoteContent(string mode, string category, string stage, int? httpStatus)
    {
        var state = new State(mode);
        var result = await new ProbeRunner().RunAsync(Key, default, vault => new RemoteMcpBroker(vault, () => new Fake(state)));
        Assert.Equal("failed", result.Status); Assert.Equal(category, result.FailureCategory);
        Assert.Equal(stage, result.FailureStage); Assert.Equal(httpStatus, result.HttpStatus);
        Assert.True(result.RevocationVerified); Assert.DoesNotContain(Key, JsonSerializer.Serialize(result));
    }
    [Fact] public async Task ActualCallerCancellationStillRevokes()
    {
        using var cancel = new CancellationTokenSource();
        var state = new State("cancel") { Cancel = cancel.Cancel };
        var result = await new ProbeRunner().RunAsync(Key, cancel.Token, vault => new RemoteMcpBroker(vault, () => new Fake(state)));
        Assert.Equal("cancelled", result.Status); Assert.True(result.RevocationVerified); Assert.Equal(1, state.Requests);
    }
    [Fact] public void DualStackDnsPinsOnlyPublicIPv4AndRejectsUnsafeARecords()
    {
        var selected = RemoteMcpBroker.SelectPublicIPv4([IPAddress.Parse("2606:4700::1111"), IPAddress.Parse("1.1.1.1")]);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), Assert.Single(selected));
        foreach (var invalid in new[] { new[] { "2606:4700::1111" }, new[] { "1.1.1.1", "127.0.0.1" }, new[] { "::ffff:127.0.0.1" }, Array.Empty<string>() })
        {
            var error = Assert.Throws<RemoteMcpFailureException>(() => RemoteMcpBroker.SelectPublicIPv4(invalid.Select(IPAddress.Parse)));
            Assert.Equal(RemoteMcpFailureCategory.DnsAddressPolicy, error.Category);
        }
    }
    private sealed class State(string mode) { public string Mode = mode; public int Requests; public Action? Cancel; }
    private sealed class Fake(State state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            state.Requests++;
            Assert.Equal("https://realtyleads.ai/mcp", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(Key, request.Headers.GetValues("X-Api-Key").Single());
            Assert.Null(request.Headers.Authorization);
            if (state.Mode is "unauthorized" or "forbidden") return new(state.Mode == "unauthorized" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden) { Content = new StringContent(Key) };
            if (state.Mode == "echo") throw new InvalidOperationException(Key);
            if (state.Mode == "cancel") { state.Cancel?.Invoke(); throw new OperationCanceledException(); }
            if (state.Mode is "dns" or "tls" or "connect") throw new HttpRequestException(state.Mode switch {
                "dns" => HttpRequestError.NameResolutionError, "tls" => HttpRequestError.SecureConnectionError, _ => HttpRequestError.ConnectionError }, Key);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement; var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            object result;
            if (method == "initialize") result = new { protocolVersion = state.Mode == "protocol" ? "unexpected" : RealtyPolicy.Protocol };
            else if (method == "tools/list") result = new { tools = new object[] {
                new { name = "search_inheritance_leads", inputSchema = new { type = "object", properties = new { state = new { type = "string" }, county = new { type = "string" }, city = new { type = "string" }, status = new { type = "string" }, minScore = new { type = "number" }, limit = new { type = "integer" } } } },
                new { name = "get_inheritance_lead_by_id", inputSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } } } } };
            else
            {
                var parameters = root.GetProperty("params");
                var search = parameters.GetProperty("name").GetString() == "search_inheritance_leads";
                var arguments = parameters.GetProperty("arguments");
                if (search) { Assert.Equal(1, arguments.GetProperty("limit").GetInt32()); Assert.Single(arguments.EnumerateObject()); }
                else { Assert.Equal("00000000-0000-0000-0000-000000000001", arguments.GetProperty("id").GetString()); Assert.Single(arguments.EnumerateObject()); }
                var lead = new { inheritanceLeadId = !search && state.Mode == "different-id" ? "00000000-0000-0000-0000-000000000002" : "00000000-0000-0000-0000-000000000001",
                    propertyId = (string?)null, address = "Fixture private address", ownerName = "Fixture owner", status = "Verified", score = 90,
                    deceasedId = (string?)null, deceasedName = (string?)null, cityName = (string?)null, countyName = (string?)null, stateAbbreviation = "TX" };
                var text = search ? JsonSerializer.Serialize(state.Mode == "empty" ? [] : state.Mode == "too-many" ? new[] { lead, lead } : [lead]) : JsonSerializer.Serialize(lead);
                result = new { content = new[] { new { type = "text", text } } };
            }
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result });
            if (state.Mode == "schema") json = json.Replace("search_inheritance_leads", "unreviewed_tool");
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
