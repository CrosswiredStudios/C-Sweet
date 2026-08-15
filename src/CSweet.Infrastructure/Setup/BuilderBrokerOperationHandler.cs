using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.ExecutionArtifacts;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;
internal sealed class BuilderBrokerOperationHandler(
    BuilderWorkloadSpecification workload,
    AgentBuildExecutionRequest request,
    BuilderArtifactBrokerStreamHandler artifact,
    IAgentBuildProgressReporter progress,
    ILogger logger) : IAgentBrokerOperationHandler
{
    private const int MaximumFetchBytes = 768 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    public Task<BrokerOperationResult> HandleAsync(BrokerOperationContext context, CancellationToken cancellationToken) =>
        context.Purpose switch
        {
            "build.fetch" => FetchAsync(context, cancellationToken),
            "build.artifact" => ReceiveArtifactAsync(context, cancellationToken),
            "build.progress" => ReportProgressAsync(context, cancellationToken),
            _ => throw new UnauthorizedAccessException("The builder broker purpose is not authorized.")
        };

    private async Task<BrokerOperationResult> FetchAsync(BrokerOperationContext context, CancellationToken cancellationToken)
    {
        var fetch = JsonSerializer.Deserialize<BuilderFetchRequest>(context.Body.Span, JsonOptions)
            ?? throw new InvalidDataException("The builder fetch request is empty.");
        if (!Uri.TryCreate(fetch.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            fetch.Offset < 0 || fetch.MaximumBytes is < 1 or > MaximumFetchBytes)
            throw new UnauthorizedAccessException("The builder fetch request is invalid.");
        if (!IsAllowed(uri)) throw new UnauthorizedAccessException("The builder requested a destination outside its build profile.");

        using var response = await SendSafeAsync(uri, fetch.Offset, fetch.MaximumBytes, cancellationToken);
        if ((int)response.StatusCode is < 200 or >= 300)
            return Result((int)response.StatusCode, ReadOnlyMemory<byte>.Empty, complete: true, response.Content.Headers.ContentType?.MediaType);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK && fetch.Offset > 0)
            await SkipExactlyAsync(input, fetch.Offset, cancellationToken);
        var buffer = new byte[fetch.MaximumBytes + 1];
        var total = 0;
        var reachedEnd = false;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                reachedEnd = true;
                break;
            }
            total += read;
        }
        var returned = Math.Min(total, fetch.MaximumBytes);
        var complete = total <= fetch.MaximumBytes && (reachedEnd || IsResponseComplete(response, fetch.Offset, returned));
        logger.LogInformation("Builder {BuildJobId} fetched {ByteCount} bytes from {Host}.", request.BuildJobId, returned, uri.Host);
        return Result(200, buffer.AsMemory(0, returned), complete, response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<BrokerOperationResult> ReceiveArtifactAsync(BrokerOperationContext context, CancellationToken cancellationToken)
    {
        var sequence = RequiredLong(context.Headers, "X-CSweet-Sequence");
        var completed = RequiredBoolean(context.Headers, "X-CSweet-Completed");
        context.Headers.TryGetValue("X-CSweet-Digest", out var digest);
        await artifact.HandleAsync(new GuestBrokerStreamContext(
            workload.WorkloadId,
            request.PackageVersionId,
            "agent-artifact",
            sequence,
            context.Body,
            completed,
            string.IsNullOrWhiteSpace(digest) ? null : digest), cancellationToken);
        return Result(200, ReadOnlyMemory<byte>.Empty, complete: true, "application/octet-stream");
    }

    private async Task<BrokerOperationResult> ReportProgressAsync(BrokerOperationContext context, CancellationToken cancellationToken)
    {
        var update = JsonSerializer.Deserialize<BuilderProgressRequest>(context.Body.Span, JsonOptions)
            ?? throw new InvalidDataException("The builder progress update is empty.");
        if (update.Step is not (AgentBuildStepKeys.Isolate or AgentBuildStepKeys.Restore or AgentBuildStepKeys.Publish or AgentBuildStepKeys.Package) ||
            update.Status is not ("started" or "succeeded" or "failed") || update.Detail.Length > 1000)
            throw new InvalidDataException("The builder progress update is invalid.");
        var status = update.Status switch
        {
            "started" => AgentBuildStepStatuses.InProgress,
            "succeeded" => AgentBuildStepStatuses.Succeeded,
            _ => AgentBuildStepStatuses.Failed
        };
        await progress.ReportAsync(new AgentBuildProgressUpdate(
            update.Step, status,
            status == AgentBuildStepStatuses.Failed ? null : update.Detail,
            status == AgentBuildStepStatuses.Failed ? update.Detail : null), cancellationToken);
        return Result(200, ReadOnlyMemory<byte>.Empty, complete: true, "application/json");
    }

    private bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return false;
        if (uri.Host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(request.RepositoryUrl, UriKind.Absolute, out var repository) ||
                !repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
            var segments = repository.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2) return false;
            var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
            var expected = $"/{segments[0]}/{name}/zip/{request.CommitSha}";
            return uri.AbsolutePath.Equals(expected, StringComparison.Ordinal);
        }
        return uri.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("globalcdn.nuget.org", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("www.nuget.org", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".nuget.org", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendSafeAsync(Uri initial, long offset, int maximumBytes, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowed(current)) throw new UnauthorizedAccessException("A builder download redirect left the approved profile.");
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.Range = new RangeHeaderValue(offset, checked(offset + maximumBytes));
            var response = await DownloadClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new IOException("The builder download redirect omitted its destination.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new IOException("The builder download exceeded its redirect limit.");
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var address = addresses.FirstOrDefault(IsPublicAddress)
            ?? throw new UnauthorizedAccessException("The builder destination did not resolve to a public address.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipv6 = address.GetAddressBytes();
            return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None) &&
                   ipv6[0] is >= 0x20 and <= 0x3f &&
                   !(ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0d && ipv6[3] == 0xb8);
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] is not (0 or 10 or 127) &&
               !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
               !(bytes[0] == 192 && bytes[1] == 168) &&
               !(bytes[0] == 192 && bytes[1] == 0) &&
               !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) &&
               !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
               !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
               !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
               bytes[0] < 224;
    }

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectPublicAsync,
            AutomaticDecompression = DecompressionMethods.None
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsResponseComplete(HttpResponseMessage response, long offset, int returned)
    {
        if (response.Content.Headers.ContentRange?.Length is { } length) return offset + returned >= length;
        if (response.Content.Headers.ContentLength is { } contentLength) return returned >= contentLength;
        return returned == 0;
    }

    private static async Task SkipExactlyAsync(Stream input, long bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (bytes > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("The upstream response ended before the requested builder offset.");
            bytes -= read;
        }
    }

    private static BrokerOperationResult Result(int status, ReadOnlyMemory<byte> body, bool complete, string? contentType) => new(
        status,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-CSweet-Complete"] = complete ? "true" : "false",
            ["X-CSweet-Content-Type"] = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
        },
        body);

    private static long RequiredLong(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed : throw new InvalidDataException($"The builder header {name} is invalid.");
    private static bool RequiredBoolean(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed)
            ? parsed : throw new InvalidDataException($"The builder header {name} is invalid.");

    private sealed record BuilderFetchRequest(string Url, long Offset, int MaximumBytes);
    private sealed record BuilderProgressRequest(string Step, string Status, string Detail);
}