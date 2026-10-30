using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.RemoteMcp;

/// <summary>Called only after the host resolves the account from an authenticated consumer binding, never from tool arguments.</summary>
/// <remarks>Endpoint, header, tool mapping and schemas come only from the installed provider's reviewed manifest.</remarks>
public sealed class RemoteMcpBroker
{
    public const int MaximumResponseBytes = 1_048_576;
    public const int MaximumListedTools = 256;
    private readonly AccountVault vault;
    private readonly ReviewedRemoteMcpProfile profile;
    private readonly Func<HttpMessageHandler> handler;
    public RemoteMcpBroker(AccountVault vault, ReviewedRemoteMcpProfile profile) : this(vault, profile, () => CreateHandler(profile.Host)) { }
    internal RemoteMcpBroker(AccountVault vault, ReviewedRemoteMcpProfile profile, Func<HttpMessageHandler> handler)
    {
        this.vault = vault; this.profile = profile ?? throw new ArgumentNullException(nameof(profile)); this.handler = handler;
    }
    public async Task<JsonElement> ReadAsync(AccountScope scope, AccountReference account, Guid consumer, string capability, JsonElement input, CancellationToken ct = default, Func<CancellationToken, Task>? revalidate = null)
    {
        var tool = profile.Tool(capability);
        profile.ValidateInput(capability, input);
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
                "tools/list" => RemoteMcpStage.ToolsList, _ => RemoteMcpStage.ToolCall };
            responseStatus = null;
            _ = await Require();
            using var request = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
            if (profile.Bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else request.Headers.Add(profile.Header, key);
            request.Headers.Add("MCP-Protocol-Version", profile.Protocol);
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
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Withheld();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > MaximumResponseBytes) throw Withheld();
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
            var initialized = await Rpc("initialize", new { protocolVersion = profile.Protocol, capabilities = new { }, clientInfo = new { name = "CSweet-trusted-broker", version = "0.2.0" } }, 1);
            if (initialized.GetProperty("protocolVersion").GetString() != profile.Protocol) throw Withheld();
            await Rpc("notifications/initialized", new { }, null);
            var listing = await Rpc("tools/list", new { }, 2);
            ValidateTools(listing, tool);
            var result = await Rpc("tools/call", new { name = tool.RemoteTool, arguments = input }, 3);
            if (result.TryGetProperty("isError", out var error) && error.GetBoolean()) throw Withheld();
            var content = result.GetProperty("content");
            if (content.GetArrayLength() != 1 || content[0].GetProperty("type").GetString() != "text") throw Withheld();
            // Closed schemas are at most eight levels deep; parsing deeper content is rejected before schema checks.
            using var output = JsonDocument.Parse(content[0].GetProperty("text").GetString()!, new JsonDocumentOptions { MaxDepth = ClosedJsonSchema.MaximumDepth + 2 });
            CheckEcho(output.RootElement, key);
            if (!profile.IsReviewedOutput(capability, output.RootElement)) throw Withheld();
            _ = await Require();
            return output.RootElement.Clone();
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
                    RemoteMcpStage.ToolCall => RemoteMcpFailureCategory.ResultSchemaRejected,
                    _ => RemoteMcpFailureCategory.ResponseRejected }, responseStatus);
        }
    }

    /// <summary>
    /// The mapped tool must be advertised exactly once with input properties compatible with the reviewed schema.
    /// Other advertised tools are ignored and can never be called: the call name comes only from the reviewed mapping.
    /// </summary>
    private static void ValidateTools(JsonElement listing, RemoteMcpToolPolicy tool)
    {
        if (listing.TryGetProperty("nextCursor", out _) || !listing.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
            tools.GetArrayLength() is 0 or > MaximumListedTools) throw Withheld();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? match = null;
        foreach (var listed in tools.EnumerateArray())
        {
            if (listed.ValueKind != JsonValueKind.Object || !listed.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !seen.Add(name.GetString()!)) throw Withheld();
            if (name.GetString() == tool.RemoteTool) match = listed;
        }
        if (match is not { } advertised || !advertised.TryGetProperty("inputSchema", out var schema) ||
            !ClosedJsonSchema.IsCompatibleRemoteInput(tool.InputSchema, schema)) throw Withheld();
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
    private static HttpMessageHandler CreateHandler(string host) => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, ct) =>
        {
            if (!string.Equals(context.DnsEndPoint.Host, host, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != 443) throw Withheld();
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
