using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Artifacts;
using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.Protocol;
using CSweet.Application.Setup;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public interface IBuilderGuestSessionCoordinator
{
    Task<IBuilderGuestSession> StartAsync(
        IsolationWorkloadHandle handle,
        BuilderWorkloadSpec workload,
        AgentBuildExecutionRequest request,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default);
}

public interface IBuilderGuestSession : IAsyncDisposable
{
    Task Completion { get; }
}

public sealed class BuilderGuestSessionCoordinator(
    IHyperVGuestTransport transport,
    IAgentArtifactStore artifacts,
    IBuilderArtifactResultPublisher results,
    ArtifactStoreOptions artifactOptions,
    TimeProvider timeProvider,
    ILogger<BuilderGuestSessionCoordinator> logger) : IBuilderGuestSessionCoordinator
{
    public async Task<IBuilderGuestSession> StartAsync(
        IsolationWorkloadHandle handle,
        BuilderWorkloadSpec workload,
        AgentBuildExecutionRequest request,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(handle.ProviderId, IsolationProviderCatalog.HyperV().ProviderId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(handle.ProviderInstanceId, "N", out var virtualMachineId) || virtualMachineId == Guid.Empty ||
            handle.WorkloadId != workload.WorkloadId || handle.Kind != IsolationWorkloadKind.Builder)
            throw new InvalidDataException("The builder guest session binding is invalid.");

        var stream = await transport.ConnectAsync(virtualMachineId, cancellationToken);
        var stagingRoot = Path.Combine(artifactOptions.ValidatedRootPath(), ".builder-streams");
        var provenance = JsonSerializer.Serialize(new
        {
            request.RepositoryUrl,
            request.CommitSha,
            request.ProjectPath,
            request.BuildProfileId,
            workload.GuestImage.Digest,
            brokerProtocolVersion = workload.BrokerLease.ProtocolVersion
        });
        var artifact = new BuilderArtifactBrokerStreamHandler(
            new BuilderArtifactStreamGrant(
                workload.WorkloadId,
                request.PackageVersionId,
                "agent-artifact",
                workload.MaximumArtifactBytes,
                "1.0",
                "linux",
                "x64",
                provenance),
            artifacts,
            results,
            stagingRoot);
        var operations = new BuilderBrokerOperationHandler(workload, request, artifact, progress, logger);
        var grant = new AgentBrokerGrant(
            workload.WorkloadId,
            workload.BrokerLease.ChannelId,
            request.PackageVersionId,
            workload.GuestImage.Digest,
            null,
            workload.BrokerLease.ProtocolVersion,
            workload.BrokerLease.BootToken,
            workload.BrokerLease.ExpiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "build.fetch", "build.artifact", "build.progress" },
            MaximumRequestCount: 100_000,
            MaximumRequestBodyBytes: 1024 * 1024,
            MaximumResponseBodyBytes: 1024 * 1024,
            MaximumFrameBytes: 16 * 1024 * 1024);
        var boot = new GuestBootConfiguration
        {
            WorkloadId = workload.WorkloadId.ToString("D"),
            ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
            ProtocolVersion = workload.BrokerLease.ProtocolVersion,
            GuestImageDigest = workload.GuestImage.Digest,
            BootToken = workload.BrokerLease.BootToken,
            LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
            ArtifactRoot = "/usr/lib/csweet/builder",
            WorkloadKind = (int)IsolationWorkloadKind.Builder,
            LocalBrokerSocketPath = "/run/csweet/broker.sock",
            WorkloadTokenPath = "/run/csweet/workload-token",
            MaximumFrameBytes = 16 * 1024 * 1024
        };
        var start = new StartCommand
        {
            WorkloadKind = (int)IsolationWorkloadKind.Builder,
            MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes
        };
        start.Entrypoint.AddRange([
            "/usr/lib/csweet/builder/CSweet.AgentRuntime.Builder",
            "--repository", request.RepositoryUrl,
            "--commit", request.CommitSha,
            "--project", request.ProjectPath,
            "--maximum-repository-bytes", checked(request.MaximumRepositorySizeMb * 1024L * 1024L).ToString(CultureInfo.InvariantCulture),
            "--maximum-artifact-bytes", workload.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture),
            "--broker-socket", "/run/csweet/broker.sock"
        ]);
        if (!string.IsNullOrWhiteSpace(request.TargetFramework))
            start.Entrypoint.AddRange(["--target-framework", request.TargetFramework]);
        var hostSession = new GuestBrokerHostSession(grant, operations, timeProvider, bootConfiguration: boot, startCommand: start);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = RunAsync(hostSession, stream, lifetime.Token);
        try
        {
            var completed = await Task.WhenAny(hostSession.Started, run).WaitAsync(cancellationToken);
            if (completed == run) await run;
            await hostSession.Started.WaitAsync(cancellationToken);
            return new ActiveBuilderGuestSession(run, lifetime, artifact, stream);
        }
        catch
        {
            await lifetime.CancelAsync();
            try { await run; } catch { }
            lifetime.Dispose();
            await artifact.DisposeAsync();
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task RunAsync(GuestBrokerHostSession session, Stream stream, CancellationToken cancellationToken) =>
        await session.RunAsync(stream, stream, cancellationToken);

    private sealed class ActiveBuilderGuestSession(
        Task completion,
        CancellationTokenSource lifetime,
        BuilderArtifactBrokerStreamHandler artifact,
        Stream stream) : IBuilderGuestSession
    {
        public Task Completion => completion;

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            try { await completion; } catch (OperationCanceledException) { }
            lifetime.Dispose();
            await artifact.DisposeAsync();
            await stream.DisposeAsync();
        }
    }
}

internal sealed class BuilderBrokerOperationHandler(
    BuilderWorkloadSpec workload,
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
