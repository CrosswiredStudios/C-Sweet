using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CSweet.RemoteMcp;

/// <summary>Called only after the host resolves the account from an authenticated consumer binding, never from tool arguments.</summary>
public sealed class RemoteMcpBroker
{
    private readonly AccountVault vault;
    private readonly Func<HttpMessageHandler> handler;
    public RemoteMcpBroker(AccountVault vault) : this(vault, CreateHandler) { }
    internal RemoteMcpBroker(AccountVault vault, Func<HttpMessageHandler> handler) { this.vault = vault; this.handler = handler; }
    public async Task<JsonElement> ReadAsync(AccountScope scope, AccountReference account, Guid consumer, string capability, JsonElement input, CancellationToken ct = default, Func<CancellationToken, Task>? revalidate = null)
    {
        RealtyPolicy.ValidateInput(capability, input);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        // Capture the generation before any network I/O, and require it at every boundary.
        async Task<string> Require()
        {
            if (revalidate is not null) await revalidate(token);
            return await vault.RequireAsync(scope, account, consumer, capability, token);
        }
        var stage = RemoteMcpStage.Initialize;
        int? responseStatus = null;
        var key = await Require();
        using var client = new HttpClient(handler(), true) { Timeout = Timeout.InfiniteTimeSpan };
        async Task<JsonElement> Rpc(string method, object args, int? id)
        {
            stage = method switch { "initialize" => RemoteMcpStage.Initialize, "notifications/initialized" => RemoteMcpStage.Initialized,
                "tools/list" => RemoteMcpStage.ToolsList, _ => capability == RealtyPolicy.Search ? RemoteMcpStage.Search : RemoteMcpStage.Lookup };
            responseStatus = null;
            _ = await Require();
            using var request = new HttpRequestMessage(HttpMethod.Post, RealtyPolicy.Endpoint);
            request.Headers.Add("X-Api-Key", key);
            request.Headers.Add("MCP-Protocol-Version", RealtyPolicy.Protocol);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Content = id is null ? JsonContent.Create(new { jsonrpc = "2.0", method, @params = args })
                : JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = args });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            responseStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new RemoteMcpFailureException(stage, responseStatus switch {
                    401 => RemoteMcpFailureCategory.AuthenticationRejected, 403 => RemoteMcpFailureCategory.AccessPolicyRejected,
                    >= 300 and < 400 => RemoteMcpFailureCategory.RedirectRejected, 429 => RemoteMcpFailureCategory.RateLimited,
                    >= 500 => RemoteMcpFailureCategory.ProviderHttpError, _ => RemoteMcpFailureCategory.HttpRejected }, responseStatus);
            if (response.Headers.Contains("Mcp-Session-Id")) throw Withheld();
            if (id is null)
            {
                if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.NoContent)) throw Withheld();
                return default;
            }
            if (response.Content.Headers.ContentLength > 1_048_576) throw Withheld();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > 1_048_576) throw Withheld();
                bytes.Write(buffer, 0, read);
            }
            var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") text = Sse(text);
            else if (response.Content.Headers.ContentType?.MediaType != "application/json") throw Withheld();
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            CheckEcho(document.RootElement, key);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(x => x.Name).Distinct().Count() != root.EnumerateObject().Count() ||
                !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
                !root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id ||
                root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result)) throw Withheld();
            _ = await Require();
            return result.Clone();
        }
        try
        {
            var initialized = await Rpc("initialize", new { protocolVersion = RealtyPolicy.Protocol, capabilities = new { }, clientInfo = new { name = "CSweet-trusted-broker", version = "0.1.0" } }, 1);
            if (initialized.GetProperty("protocolVersion").GetString() != RealtyPolicy.Protocol) throw Withheld();
            await Rpc("notifications/initialized", new { }, null);
            var listing = await Rpc("tools/list", new { }, 2);
            ValidateTools(listing);
            var result = await Rpc("tools/call", new { name = RealtyPolicy.RemoteName(capability), arguments = input }, 3);
            if (result.TryGetProperty("isError", out var error) && error.GetBoolean()) throw Withheld();
            var content = result.GetProperty("content");
            if (content.GetArrayLength() != 1 || content[0].GetProperty("type").GetString() != "text") throw Withheld();
            using var leads = JsonDocument.Parse(content[0].GetProperty("text").GetString()!, new JsonDocumentOptions { MaxDepth = 8 });
            CheckEcho(leads.RootElement, key);
            ValidateLeads(leads.RootElement, capability);
            _ = await Require();
            return leads.RootElement.Clone();
        }
        catch (RemoteMcpFailureException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new RemoteMcpFailureException(stage, RemoteMcpFailureCategory.Timeout, responseStatus); }
        catch (HttpRequestException e)
        {
            // Inspect only exception types/status enums, never messages, data, response bodies or headers.
            for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
                if (inner is RemoteMcpFailureException safe) throw new RemoteMcpFailureException(stage, safe.Category);
            throw new RemoteMcpFailureException(stage, e.HttpRequestError switch {
                HttpRequestError.NameResolutionError => RemoteMcpFailureCategory.DnsFailure,
                HttpRequestError.SecureConnectionError => RemoteMcpFailureCategory.TlsFailure,
                HttpRequestError.ConnectionError => RemoteMcpFailureCategory.ConnectionFailure,
                _ => RemoteMcpFailureCategory.TransportFailure });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException or IOException)
        {
            throw new RemoteMcpFailureException(stage, e is IOException ? RemoteMcpFailureCategory.TransportFailure :
                e is JsonException ? RemoteMcpFailureCategory.InvalidJson : stage switch {
                    RemoteMcpStage.Initialize => RemoteMcpFailureCategory.ProtocolMismatch,
                    RemoteMcpStage.ToolsList => RemoteMcpFailureCategory.ToolSchemaRejected,
                    RemoteMcpStage.Search or RemoteMcpStage.Lookup => RemoteMcpFailureCategory.ResultSchemaRejected,
                    _ => RemoteMcpFailureCategory.ResponseRejected }, responseStatus);
        }
    }

    private static void ValidateTools(JsonElement listing)
    {
        if (listing.TryGetProperty("nextCursor", out _) || !listing.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 2) throw Withheld();
        var seen = new HashSet<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            if (!seen.Add(name) || name is not ("search_inheritance_leads" or "get_inheritance_lead_by_id")) throw Withheld();
            var schema = tool.GetProperty("inputSchema");
            if (schema.GetProperty("type").GetString() != "object") throw Withheld();
            var properties = schema.GetProperty("properties");
            var expected = name == "get_inheritance_lead_by_id" ? new[] { "id" } : ["state", "county", "city", "status", "minScore", "limit"];
            if (properties.ValueKind != JsonValueKind.Object || !properties.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(expected.Order())) throw Withheld();
            foreach (var p in properties.EnumerateObject())
            {
                var type = p.Value.GetProperty("type");
                var expectedType = p.Name == "limit" ? "integer" : p.Name == "minScore" ? "number" : "string";
                if (type.ValueKind == JsonValueKind.String ? type.GetString() != expectedType :
                    type.ValueKind != JsonValueKind.Array || !type.EnumerateArray().Select(x => x.GetString()).Order().SequenceEqual(new[] { expectedType, "null" }.Order())) throw Withheld();
            }
            if (name == "get_inheritance_lead_by_id" && (!schema.TryGetProperty("required", out var required) || required.GetArrayLength() != 1 || required[0].GetString() != "id")) throw Withheld();
        }
    }
    private static void ValidateLeads(JsonElement root, string capability)
    {
        if (capability == RealtyPolicy.Search)
        {
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 100) throw Withheld();
            foreach (var lead in root.EnumerateArray()) ValidateLead(lead);
        }
        else ValidateLead(root);
    }
    private static void ValidateLead(JsonElement lead)
    {
        string[] fields = ["inheritanceLeadId", "propertyId", "address", "ownerName", "status", "score", "deceasedId", "deceasedName", "cityName", "countyName", "stateAbbreviation"];
        if (lead.ValueKind != JsonValueKind.Object || !lead.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(fields.Order())) throw Withheld();
        foreach (var p in lead.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Null) continue;
            if (p.Name == "score")
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var score) || !double.IsFinite(score)) throw Withheld();
            }
            else if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > 4096) throw Withheld();
        }
    }
    private static void CheckEcho(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.String) Check(element.GetString()!);
        else if (element.ValueKind == JsonValueKind.Number) Check(element.GetRawText());
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckEcho(child, key);
        else if (element.ValueKind == JsonValueKind.Object) foreach (var p in element.EnumerateObject()) { Check(p.Name); CheckEcho(p.Value, key); }
        void Check(string text)
        {
            if (text.Contains(key, StringComparison.Ordinal) || text.Contains(Uri.EscapeDataString(key), StringComparison.OrdinalIgnoreCase) ||
                text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)), StringComparison.Ordinal)) throw Withheld();
        }
    }
    private static string Sse(string text)
    {
        // Stateless request/response only: one bounded message, no notifications, replay IDs or reconnects.
        var data = new List<string>(); var completed = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) { if (data.Count > 0) completed = true; continue; }
            if (line.StartsWith(':')) continue;
            if (completed) throw Withheld();
            if (line == "event: message") continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) throw Withheld();
            data.Add(line[5..].TrimStart(' '));
        }
        if (data.Count == 0 || !completed) throw Withheld();
        return string.Join('\n', data);
    }
    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, ct) =>
        {
            if (context.DnsEndPoint.Host != "realtyleads.ai" || context.DnsEndPoint.Port != 443) throw Withheld();
            var addresses = SelectPublicIPv4(await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct));
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
    internal static IPAddress[] SelectPublicIPv4(IEnumerable<IPAddress> addresses)
    {
        // AAAA answers do not make a public A answer unsafe. Never connect to an IPv6 answer.
        var ipv4 = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
        if (ipv4.Length == 0 || ipv4.Any(a => !Public(a)))
            throw new RemoteMcpFailureException(RemoteMcpStage.Dns, RemoteMcpFailureCategory.DnsAddressPolicy);
        return ipv4;
    }
    private static bool Public(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false; // First release deliberately IPv4 only.
        return !ForbiddenNetworks.Any(network => network.Contains(ip));
    }
    private static readonly System.Net.IPNetwork[] ForbiddenNetworks =
        new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
            "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16",
            "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4" }
        .Select(System.Net.IPNetwork.Parse).ToArray();

    private static InvalidOperationException Withheld() => new("The provider response was withheld. Check the account connection in native settings.");
}
