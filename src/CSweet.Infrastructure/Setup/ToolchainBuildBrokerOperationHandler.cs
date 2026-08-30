using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.ExecutionArtifacts;
using CSweet.Office.Contracts.Workloads;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

/// <summary>Combines the normal agent MCP boundary with narrowly brokered source and output transfer.</summary>
internal sealed class ToolchainBuildBrokerOperationHandler(
    ToolchainBuildWorkloadSpecification workload,
    IAgentBrokerOperationHandler runtime,
    BuilderArtifactBrokerStreamHandler artifact,
    Func<CancellationToken, Task> artifactCompleted,
    Func<CancellationToken, Task<ToolchainSourceArchive?>>? prepareTrustedSource,
    ILogger logger) : IAgentBrokerOperationHandler
{
    private const int MaximumFetchBytes = 768 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient DownloadClient = CreateDownloadClient();
    private ToolchainSourceArchive? trustedSource;

    public Task<BrokerOperationResult> HandleAsync(BrokerOperationContext context, CancellationToken cancellationToken) =>
        context.Purpose switch
        {
            "mcp.runtime" => runtime.HandleAsync(context, cancellationToken),
            "build.fetch" => FetchAsync(context, cancellationToken),
            "build.artifact" => ReceiveArtifactAsync(context, cancellationToken),
            "build.progress" => ProgressAsync(context),
            _ => throw new UnauthorizedAccessException("The toolchain broker purpose is not authorized.")
        };

    private async Task<BrokerOperationResult> FetchAsync(BrokerOperationContext context, CancellationToken cancellationToken)
    {
        var fetch = JsonSerializer.Deserialize<FetchRequest>(context.Body.Span, JsonOptions)
            ?? throw new InvalidDataException("The source fetch request is empty.");
        if (!Uri.TryCreate(fetch.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            fetch.Offset < 0 || fetch.MaximumBytes is < 1 or > MaximumFetchBytes || !IsAllowedSource(uri))
            throw new UnauthorizedAccessException("The source fetch request is outside the exact repository revision.");

        if (prepareTrustedSource is not null)
        {
            trustedSource ??= await prepareTrustedSource(cancellationToken);
            if (trustedSource is not null)
                return FetchTrustedSource(fetch, trustedSource);
        }

        using var response = await SendSafeAsync(uri, fetch.Offset, fetch.MaximumBytes, cancellationToken);
        if ((int)response.StatusCode is < 200 or >= 300)
            return Result((int)response.StatusCode, ReadOnlyMemory<byte>.Empty, true,
                response.Content.Headers.ContentType?.MediaType);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK && fetch.Offset > 0)
            await SkipExactlyAsync(input, fetch.Offset, cancellationToken);
        var buffer = new byte[fetch.MaximumBytes + 1];
        var total = 0;
        var reachedEnd = false;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) { reachedEnd = true; break; }
            total += read;
        }
        var returned = Math.Min(total, fetch.MaximumBytes);
        if (checked(fetch.Offset + returned) > workload.MaximumSourceBytes)
            throw new InvalidDataException("The exact source archive exceeds its certified byte limit.");
        var complete = total <= fetch.MaximumBytes && (reachedEnd ||
            response.Content.Headers.ContentRange?.Length is { } length && fetch.Offset + returned >= length);
        logger.LogInformation("Toolchain build {BuildId} fetched {ByteCount} source bytes from {Host}.",
            workload.DeliveryBuildId, returned, uri.Host);
        return Result(200, buffer.AsMemory(0, returned), complete, response.Content.Headers.ContentType?.MediaType);
    }

    private BrokerOperationResult FetchTrustedSource(FetchRequest fetch, ToolchainSourceArchive source)
    {
        if (source.Archive.LongLength > workload.MaximumSourceBytes ||
            source.Sha256.Length != 64 || source.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The trusted exact-revision source archive is invalid or exceeds its approved limit.");
        if (fetch.Offset > source.Archive.LongLength)
            throw new InvalidDataException("The source fetch offset exceeds the trusted archive.");
        var available = source.Archive.LongLength - fetch.Offset;
        var count = (int)Math.Min(available, fetch.MaximumBytes);
        var body = count == 0
            ? ReadOnlyMemory<byte>.Empty
            : source.Archive.AsMemory(checked((int)fetch.Offset), count);
        var complete = fetch.Offset + count == source.Archive.LongLength;
        var result = Result(200, body, complete, "application/zip", "flat", source.Sha256);
        logger.LogInformation("Toolchain build {BuildId} fetched {ByteCount} source bytes from the credential-isolated Git host.",
            workload.DeliveryBuildId, count);
        return result;
    }

    private async Task<BrokerOperationResult> ReceiveArtifactAsync(BrokerOperationContext context, CancellationToken cancellationToken)
    {
        var sequence = RequiredLong(context.Headers, "X-CSweet-Sequence");
        var completed = RequiredBoolean(context.Headers, "X-CSweet-Completed");
        context.Headers.TryGetValue("X-CSweet-Digest", out var digest);
        await artifact.HandleAsync(new GuestBrokerStreamContext(
            workload.WorkloadId,
            workload.Identity.InstallationId,
            "toolchain-output",
            sequence,
            context.Body,
            completed,
            string.IsNullOrWhiteSpace(digest) ? null : digest), cancellationToken);
        if (completed) await artifactCompleted(cancellationToken);
        return Result(200, ReadOnlyMemory<byte>.Empty, true, "application/octet-stream");
    }

    private Task<BrokerOperationResult> ProgressAsync(BrokerOperationContext context)
    {
        if (context.Body.Length > 16 * 1024)
            throw new InvalidDataException("The toolchain progress update is too large.");
        logger.LogDebug("Toolchain build {BuildId} reported bounded execution progress.", workload.DeliveryBuildId);
        return Task.FromResult(Result(200, ReadOnlyMemory<byte>.Empty, true, "application/json"));
    }

    private bool IsAllowedSource(Uri uri)
    {
        if (!uri.Host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(workload.SourceRepository.RepositoryUrl, UriKind.Absolute, out var repository) ||
            !repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = repository.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2) return false;
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return uri.AbsolutePath.Equals(
            $"/{segments[0]}/{name}/zip/{workload.SourceRepository.CommitSha}", StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> SendSafeAsync(Uri uri, long offset, int maximumBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(offset, checked(offset + maximumBytes));
        var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            response.Dispose();
            throw new UnauthorizedAccessException("Source archive redirects are not permitted.");
        }
        return response;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        var address = addresses.FirstOrDefault(BuilderBrokerOperationHandler.IsPublicAddress)
            ?? throw new UnauthorizedAccessException("The source host did not resolve to a public address.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }

    private static HttpClient CreateDownloadClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = ConnectPublicAsync,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task SkipExactlyAsync(Stream input, long bytes, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (bytes > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(bytes, buffer.Length)), token);
            if (read == 0) throw new EndOfStreamException("The source response ended before its requested offset.");
            bytes -= read;
        }
    }

    private static BrokerOperationResult Result(
        int status,
        ReadOnlyMemory<byte> body,
        bool complete,
        string? contentType,
        string? archiveLayout = null,
        string? sourceSha256 = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-CSweet-Complete"] = complete ? "true" : "false",
            ["X-CSweet-Content-Type"] = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
        };
        if (archiveLayout is not null) headers["X-CSweet-Archive-Layout"] = archiveLayout;
        if (sourceSha256 is not null) headers["X-CSweet-Source-Sha256"] = sourceSha256;
        return new BrokerOperationResult(status, headers, body);
    }

    private static long RequiredLong(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed : throw new InvalidDataException($"The toolchain header {name} is invalid.");
    private static bool RequiredBoolean(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed)
            ? parsed : throw new InvalidDataException($"The toolchain header {name} is invalid.");

    private sealed record FetchRequest(string Url, long Offset, int MaximumBytes);
}

internal sealed record ToolchainSourceArchive(byte[] Archive, string Sha256);
