using System.Collections.Concurrent;
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
    public int MaximumResponseBytes { get; set; } = 16 * 1024 * 1024;

    public Uri ValidatedBaseUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "https+http"))
            throw new InvalidOperationException("The AgentHost broker base URL is invalid.");
        if (TimeoutSeconds is < 1 or > 300 || MaximumResponseBytes is < 1 or > 64 * 1024 * 1024)
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
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
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
                logger.LogWarning(
                    "AgentHost broker request {RequestId} for workload {WorkloadId} returned HTTP {StatusCode}.",
                    request.RequestId,
                    request.WorkloadId,
                    (int)response.StatusCode);
            }
            return new BrokerOperationResult((int)response.StatusCode, headers, body);
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
