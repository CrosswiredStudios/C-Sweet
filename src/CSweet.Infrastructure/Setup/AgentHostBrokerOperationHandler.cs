using System.Collections.Concurrent;
using System.Text.Json;
using CSweet.AgentBroker;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentHostBrokerOptions
{
    public const string SectionName = "CSweet:AgentRuntime:AgentHostBroker";
    public string BaseUrl { get; set; } = "https+http://_mcp.agenthost";
    // The AgentHost allows LLM requests to run for up to two minutes. Leave
    // enough forwarding headroom for that request and response to complete.
    public int TimeoutSeconds { get; set; } = 180;
    // Control-plane calls must fail early enough for the SDK to reconnect before the
    // runtime startup circuit breaker fires. Capability calls retain the longer timeout.
    public int ControlRequestTimeoutSeconds { get; set; } = 10;
    public int MaximumResponseBytes { get; set; } = 16 * 1024 * 1024;

    public Uri ValidatedBaseUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "https+http"))
            throw new InvalidOperationException("The AgentHost broker base URL is invalid.");
        if (TimeoutSeconds is < 1 or > 300 ||
            ControlRequestTimeoutSeconds is < 1 or > 120 ||
            ControlRequestTimeoutSeconds >= TimeoutSeconds ||
            MaximumResponseBytes is < 1 or > 64 * 1024 * 1024)
            throw new InvalidOperationException("The AgentHost broker limits are invalid.");
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}

public sealed class AgentHostBrokerOperationHandler(
    IHttpClientFactory httpClientFactory,
    AgentHostBrokerOptions options,
    ILogger<AgentHostBrokerOperationHandler> logger) : IAgentBrokerOperationHandler
{
    private readonly ConcurrentDictionary<Guid, byte> _observedWorkloads = new();

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public async Task<BrokerOperationResult> HandleAsync(
        BrokerOperationContext request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Purpose, "mcp.runtime", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The requested broker purpose is not available through AgentHost.");
        if (_observedWorkloads.TryAdd(request.WorkloadId, 0))
        {
            logger.LogInformation(
                "Received the first authenticated MCP broker request for workload {WorkloadId}, installation {InstallationId}.",
                request.WorkloadId,
                request.InstallationId);
        }
        var client = httpClientFactory.CreateClient(nameof(AgentHostBrokerOperationHandler));
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path);
        if (!request.Body.IsEmpty)
            message.Content = new ReadOnlyMemoryContent(request.Body);
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                HopByHopHeaders.Contains(header.Key))
                continue;
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ResolveTimeoutSeconds(request.Body)));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "AgentHost forwarding failed for authenticated workload {WorkloadId}, request {RequestId}.",
                request.WorkloadId,
                request.RequestId);
            throw;
        }
        using (response)
        {
            var body = await ReadBoundedAsync(
                response.Content,
                options.MaximumResponseBytes,
                timeout.Token);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .Where(header =>
                    !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    !HopByHopHeaders.Contains(header.Key))
                .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "AgentHost broker request {RequestId} for workload {WorkloadId} completed with HTTP {StatusCode}.",
                    request.RequestId,
                    request.WorkloadId,
                    (int)response.StatusCode);
            }
            else
            {
                var error = DescribeErrorResponse(body);
                logger.LogWarning(
                    "AgentHost broker request {RequestId} for workload {WorkloadId} returned HTTP {StatusCode}: {Error}",
                    request.RequestId,
                    request.WorkloadId,
                    (int)response.StatusCode,
                    error ?? "No structured error reason was returned.");
            }
            return new BrokerOperationResult((int)response.StatusCode, headers, body);
        }
    }

    internal int ResolveTimeoutSeconds(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String &&
                method.GetString() is "initialize" or "ping" or "csweet/session/renew" or
                    "csweet/work/claim" or "csweet/work/renew" or "csweet/work/progress" or
                    "csweet/work/complete" or "csweet/work/fail" or "csweet/runtime/complete")
                return options.ControlRequestTimeoutSeconds;
        }
        catch (JsonException)
        {
            // AgentHost owns JSON-RPC validation. Preserve the established forwarding
            // behavior for malformed or future request shapes.
        }
        return options.TimeoutSeconds;
    }

    internal static string? DescribeErrorResponse(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.String)
                return null;
            var value = message.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = new string(value.Select(character =>
                char.IsControl(character) ? ' ' : character).ToArray()).Trim();
            return value.Length <= 512 ? value : value[..512];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var remaining = maximumBytes + 1 - checked((int)output.Length);
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > maximumBytes)
                throw new InvalidDataException("AgentHost returned an oversized broker response.");
        }
    }
}
