using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

/// <summary>Shared search and page reading. Public page transport retains the existing web policy checks.</summary>
public sealed class ResearchCapabilityHandler(PlatformWebProxyCapabilityHandler web, IConfiguration configuration,
    IHttpClientFactory clients) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => capability is ResearchCapabilities.Search or ResearchCapabilities.Read;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        CapabilityResult result;
        try
        {
            if (!CanHandle(request.Capability) || !session.Grant.RequestedCapabilities.Contains(request.Capability))
                throw new UnauthorizedAccessException("This research capability has not been granted.");
            if (request.Payload.Length > 16384) throw new ArgumentException("Research input exceeds its bound.");
            if (request.Capability == ResearchCapabilities.Search)
                result = Success(request, await SearchAsync(request.Payload.ToElement().Deserialize<WebSearchRequest>(Json)!, token));
            else
            {
                var input = request.Payload.ToElement().Deserialize<WebPageRequest>(Json) ?? throw new ArgumentException("A page is required.");
                if (input.MaximumCharacters is < 1000 or > 50000) throw new ArgumentException("Invalid page text limit.");
                var fetched = await web.HandleAsync(session, new RequestCapability
                {
                    RequestId = request.RequestId, Capability = PluginPlatformCapabilities.WebFetch,
                    Payload = JsonPayload.From(new PlatformWebFetchRequest(input.Url), Json)
                }, token);
                result = fetched.Succeeded
                    ? Success(request, Extract(fetched.Payload.ToElement().Deserialize<PlatformWebFetchResponse>(Json)!, input.MaximumCharacters))
                    : fetched;
            }
        }
        catch (Exception e) when (e is ArgumentException or JsonException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            result = Failure(request, e is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied : PlatformCapabilityErrorCode.ValidationFailed,
                e is HttpRequestException ? "Public search is unavailable. Retry later or use a known documentation URL." :
                e is UnauthorizedAccessException ? "Research access is not granted." : "Research input or response is invalid.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            result = Failure(request, PlatformCapabilityErrorCode.ValidationFailed, "Research timed out. Retry later or use a known documentation URL.");
        }
        yield return result;
    }

    private async Task<WebSearchResult> SearchAsync(WebSearchRequest input, CancellationToken token)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > 2000 || input.Limit is < 1 or > 10)
            throw new ArgumentException("A bounded query is required.");
        var configured = configuration["CSweet:Search:Searxng:BaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The administrator-managed search service is unavailable.");
        // The origin is administrator configured. No model-supplied URL can target internal services.
        var uri = new Uri(endpoint.ToString().TrimEnd('/') + "/search?q=" + Uri.EscapeDataString(input.Query) + "&format=json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = clients.CreateClient("searxng-research");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[1024 * 1024 + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token);
            if (read == 0) break;
            count += read;
        }
        if (count == buffer.Length) throw new InvalidOperationException("Search response exceeds its bound.");
        using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
        return ParseSearch(document.RootElement, input.Limit);
    }

    internal static WebSearchResult ParseSearch(JsonElement data, int limit)
    {
        var results = new List<WebSearchHit>();
        if (data.TryGetProperty("results", out var hits))
            foreach (var hit in hits.EnumerateArray())
            {
                var url = hit.TryGetProperty("url", out var value) ? value.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                    !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback ||
                    IPAddress.TryParse(uri.Host, out var ip) && OutboundNetworkPolicy.IsForbiddenAddress(ip)) continue;
                results.Add(new(Clip(hit, "title", 1000), uri.AbsoluteUri, Clip(hit, "content", 4000)));
                if (results.Count == limit) break;
            }
        var failures = data.TryGetProperty("unresponsive_engines", out var engines)
            ? engines.EnumerateArray().Take(20).Select(x => x.ValueKind == JsonValueKind.Array
                ? string.Join(": ", x.EnumerateArray().Take(2).Select(y => y.ToString())) : "An upstream engine was unavailable.")
                .Select(x => x[..Math.Min(x.Length, 200)]).ToArray() : [];
        return new(results, failures);
    }

    private static string Clip(JsonElement element, string key, int length)
    {
        var text = element.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        return text[..Math.Min(text.Length, length)];
    }

    internal static WebPageResult Extract(PlatformWebFetchResponse page, int maximumCharacters)
    {
        if (page.StatusCode is < 200 or >= 300)
            return new(page.FinalUrl, "", "", page.Truncated,
                $"The public page returned HTTP {page.StatusCode}. No page content was used. " +
                "Search for the current documentation URL or use another authoritative source.");
        var contentType = page.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (contentType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
            return new(page.FinalUrl, "", "", page.Truncated, "Only public HTML and plain text are supported.");
        var html = Encoding.UTF8.GetString(page.Body);
        var text = html;
        var title = "";
        string? limitation = null;
        if (contentType != "text/plain")
        {
            var reader = new SmartReader.Reader(page.FinalUrl, html) { MaxElemsToParse = 100000 };
            var article = reader.GetArticle(); // Parses supplied bytes only; no page/image downloads.
            title = article.Title ?? "";
            text = article.TextContent ?? "";
            if (string.IsNullOrWhiteSpace(text)) limitation = "No readable static content was found. JavaScript-only pages require another source.";
        }
        return new(page.FinalUrl, title, text[..Math.Min(text.Length, maximumCharacters)], page.Truncated || text.Length > maximumCharacters, limitation);
    }

    private static CapabilityResult Success(RequestCapability request, object data) => new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.From(data, Json) };
    private static CapabilityResult Failure(RequestCapability request, PlatformCapabilityErrorCode code, string message) => new() { RequestId = request.RequestId, FailureCode = code == PlatformCapabilityErrorCode.Denied ? "denied" : "validation_failed", Error = message };
}
