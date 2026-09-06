using CSweet.Infrastructure.Setup;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformWebSocketCapabilityHandler(
    CSweetDbContext db,
    IPluginSecretStore secrets,
    IAuditEventWriter audit) : IAsyncDisposable
{
    private const int MaximumFrameBytes = 256 * 1024;
    private const int MaximumConnectionsPerInstallation = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SocketState> _connections = new(StringComparer.Ordinal);

    public async Task<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, CancellationToken cancellationToken)
    {
        if (request.Capability != PluginPlatformCapabilities.WebSocket ||
            session.Grant.RequestedCapabilities?.Contains(PluginPlatformCapabilities.WebSocket) != true)
            return Failure(request.RequestId, "The installation is not granted web.socket.v1.");
        if (request.Payload.Length > MaximumFrameBytes)
            return Failure(request.RequestId, "The WebSocket request exceeds the 256 KB limit.");
        if (!Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, "The plugin installation identity is invalid.");

        PlatformWebSocketRequest? input;
        try { input = JsonSerializer.Deserialize<PlatformWebSocketRequest>(request.Payload.Span, JsonOptions); }
        catch (JsonException) { return Failure(request.RequestId, "The WebSocket request is not valid JSON."); }
        if (input is null) return Failure(request.RequestId, "The WebSocket request is empty.");

        try
        {
            return input.Operation.ToLowerInvariant() switch
            {
                "connect" => await ConnectAsync(request.RequestId, installationId, input, cancellationToken),
                "send" => await SendAsync(request.RequestId, installationId, input, cancellationToken),
                "receive" => await ReceiveAsync(request.RequestId, installationId, input, cancellationToken),
                "close" => await CloseAsync(request.RequestId, installationId, input, cancellationToken),
                _ => Failure(request.RequestId, "WebSocket operation must be connect, send, receive, or close.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(request.RequestId, "The WebSocket operation timed out.");
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or SocketException)
        {
            return Failure(request.RequestId, "The remote WebSocket operation failed.");
        }
    }

    private async Task<CapabilityResult> ConnectAsync(string requestId, Guid installationId, PlatformWebSocketRequest input, CancellationToken token)
    {
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
            return Failure(requestId, "An absolute public wss URL is required.");
        var policy = await LoadPolicyAsync(installationId, token);
        if (policy is null) return Failure(requestId, "The plugin installation is not active.");
        if (_connections.Values.Count(x => x.InstallationId == installationId) >= MaximumConnectionsPerInstallation)
            return Failure(requestId, $"The installation has reached its {MaximumConnectionsPerInstallation}-connection WebSocket limit.");
        var rule = policy.Value.Manifest.WebAccess.Rules.SingleOrDefault(candidate =>
            policy.Value.Grants.Contains(CSweet.Infrastructure.Setup.AgentImportPreviewService.WebGrantToken(candidate)) &&
            candidate.Protocol == "websocket" && candidate.Scheme == "wss" &&
            string.Equals(OutboundNetworkPolicy.NormalizeHost(candidate.Host), OutboundNetworkPolicy.NormalizeHost(uri.DnsSafeHost), StringComparison.Ordinal) &&
            (candidate.Port is null || candidate.Port == uri.Port) &&
            OutboundNetworkPolicy.IsPathWithinPrefix(uri.AbsolutePath, candidate.PathPrefix) &&
            string.Equals(candidate.Credential, input.Credential, StringComparison.Ordinal));
        if (rule is null) return Failure(requestId, "Destination is outside the approved WebSocket grant.");
        if (!string.IsNullOrWhiteSpace(input.Credential))
        {
            var credential = policy.Value.Manifest.Credentials.SingleOrDefault(x => x.Name == input.Credential);
            if (credential is null || !OutboundNetworkPolicy.IsAllowedOrigin(uri, credential.AllowedOrigins))
                return Failure(requestId, "The requested credential is not bound to this origin.");
        }

        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, token);
        if (addresses.Length == 0 || addresses.Any(x => OutboundNetworkPolicy.IsForbiddenAddress(x, policy.Value.BlockedCidrs)))
            return Failure(requestId, "Destination resolves to a private or reserved address.");

        var handler = CreatePinnedHandler(uri, addresses[0]);
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await socket.ConnectAsync(uri, invoker, timeout.Token); }
        catch { socket.Dispose(); invoker.Dispose(); throw; }

        var id = Guid.NewGuid().ToString("N");
        _connections[id] = new SocketState(installationId, socket, invoker, input.Credential);
        await audit.WriteAsync("plugin.web.socket.connected", "PluginInstallation", installationId,
            $"Plugin opened WebSocket {uri.Scheme}://{uri.Host}{uri.AbsolutePath}.",
            JsonSerializer.Serialize(new { installationId, connectionId = id, host = uri.Host, path = uri.AbsolutePath }), token);
        return Success(requestId, new PlatformWebSocketResponse(id));
    }

    private async Task<CapabilityResult> SendAsync(string requestId, Guid installationId, PlatformWebSocketRequest input, CancellationToken token)
    {
        var state = await GetActiveStateAsync(installationId, input.ConnectionId, token);
        if (state is null) return Failure(requestId, "The WebSocket connection is unavailable or revoked.");
        var payload = input.Payload ?? [];
        if (payload.Length > MaximumFrameBytes) return Failure(requestId, "The WebSocket frame exceeds the 256 KB limit.");
        if (!string.IsNullOrWhiteSpace(state.Credential))
        {
            if (input.MessageType != "text") return Failure(requestId, "Credential substitution is allowed only in text frames.");
            var text = Encoding.UTF8.GetString(payload);
            if (text.Contains("{{credential}}", StringComparison.Ordinal))
            {
                var secret = await secrets.GetAsync(installationId, state.Credential, token);
                if (string.IsNullOrWhiteSpace(secret)) return Failure(requestId, "The bound WebSocket credential is not configured.");
                payload = Encoding.UTF8.GetBytes(text.Replace("{{credential}}", secret, StringComparison.Ordinal));
            }
        }
        await state.SendGate.WaitAsync(token);
        try
        {
            var type = input.MessageType == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
            await state.Socket.SendAsync(payload, type, true, token);
        }
        finally { state.SendGate.Release(); }
        return Success(requestId, new PlatformWebSocketResponse(input.ConnectionId!));
    }

    private async Task<CapabilityResult> ReceiveAsync(string requestId, Guid installationId, PlatformWebSocketRequest input, CancellationToken token)
    {
        var state = await GetActiveStateAsync(installationId, input.ConnectionId, token);
        if (state is null) return Failure(requestId, "The WebSocket connection is unavailable or revoked.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await state.ReceiveGate.WaitAsync(timeout.Token);
        try
        {
            using var output = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                var buffer = new byte[Math.Min(16 * 1024, MaximumFrameBytes - (int)output.Length + 1)];
                result = await state.Socket.ReceiveAsync(buffer, timeout.Token);
                if (result.Count + output.Length > MaximumFrameBytes)
                {
                    await RemoveAsync(input.ConnectionId!, state, WebSocketCloseStatus.MessageTooBig, "Frame limit exceeded", token);
                    return Failure(requestId, "The remote WebSocket frame exceeded the 256 KB limit.");
                }
                output.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
            return Success(requestId, new PlatformWebSocketResponse(input.ConnectionId!, output.ToArray(),
                result.MessageType == WebSocketMessageType.Binary ? "binary" : "text", result.EndOfMessage,
                result.CloseStatus is null ? null : (int)result.CloseStatus, result.CloseStatusDescription));
        }
        finally { state.ReceiveGate.Release(); }
    }

    private async Task<CapabilityResult> CloseAsync(string requestId, Guid installationId, PlatformWebSocketRequest input, CancellationToken token)
    {
        if (input.ConnectionId is null || !_connections.TryRemove(input.ConnectionId, out var state) || state.InstallationId != installationId)
            return Failure(requestId, "The WebSocket connection does not exist.");
        await state.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Plugin requested close", token);
        return Success(requestId, new PlatformWebSocketResponse(input.ConnectionId, CloseStatus: 1000));
    }

    private async Task<SocketState?> GetActiveStateAsync(Guid installationId, string? connectionId, CancellationToken token)
    {
        if (connectionId is null || !_connections.TryGetValue(connectionId, out var state) || state.InstallationId != installationId)
            return null;
        var active = await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token);
        if (active) return state;
        if (_connections.TryRemove(connectionId, out _)) await state.DisposeAsync(WebSocketCloseStatus.PolicyViolation, "Plugin grant revoked", token);
        return null;
    }

    private async Task<(PluginManifest Manifest, HashSet<string> Grants, IReadOnlyList<OutboundNetworkPolicy.CidrRange> BlockedCidrs)?> LoadPolicyAsync(Guid installationId, CancellationToken token)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token);
        if (installation?.PackageVersion is null || installation.Grant is null) return null;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion.ManifestJson, JsonOptions);
        var grants = JsonSerializer.Deserialize<IReadOnlyList<string>>(installation.Grant.NetworkAccessJson, JsonOptions);
        var blocked = await db.AgentRuntimeGlobalSettings.AsNoTracking()
            .Select(x => x.BlockedNetworkCidrs)
            .SingleOrDefaultAsync(token);
        return manifest is null ? null : (manifest, (grants ?? []).ToHashSet(StringComparer.Ordinal), OutboundNetworkPolicy.ParseCidrs(blocked));
    }

    private async Task RemoveAsync(string id, SocketState state, WebSocketCloseStatus status, string description, CancellationToken token)
    {
        _connections.TryRemove(id, out _);
        await state.DisposeAsync(status, description, token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _connections.ToArray())
            if (_connections.TryRemove(pair.Key, out var state))
                await state.DisposeAsync(WebSocketCloseStatus.EndpointUnavailable, "MCP session ended", CancellationToken.None);
    }

    private static SocketsHttpHandler CreatePinnedHandler(Uri uri, IPAddress address) => new()
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 1,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, uri.Port), token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    };

    private static CapabilityResult Success(string requestId, PlatformWebSocketResponse response) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions))
    };
    private static CapabilityResult Failure(string requestId, string error) => new()
        { RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error };

    private sealed record SocketState(Guid InstallationId, ClientWebSocket Socket, IDisposable Transport, string? Credential)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public SemaphoreSlim ReceiveGate { get; } = new(1, 1);
        public async Task DisposeAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        {
            try { if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await Socket.CloseAsync(status, description, token); }
            catch { }
            Socket.Dispose(); Transport.Dispose(); SendGate.Dispose(); ReceiveGate.Dispose();
        }
    }
}
