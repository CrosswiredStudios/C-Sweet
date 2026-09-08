using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public interface IConnectorMediaTransport
{
    Task<ConnectorMediaResponse> SendAsync(FrozenConnectorPlan plan, ConnectorMediaStep step,
        string? sessionLocation, long offset, ReadOnlyMemory<byte> chunk, long maximumSentBytes,
        Func<CancellationToken, Task> revalidate, CancellationToken ct);
}

/// <summary>Host-only bounded exchanges. A durable worker, not this transport, owns retry decisions.</summary>
public sealed class ConnectorMediaTransport(CSweetDbContext db, IPluginOAuthTokenBroker tokens) : IConnectorMediaTransport
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task<ConnectorMediaResponse> SendAsync(FrozenConnectorPlan plan, ConnectorMediaStep step,
        string? sessionLocation, long offset, ReadOnlyMemory<byte> chunk, long maximumSentBytes,
        Func<CancellationToken, Task> revalidate, CancellationToken ct)
    {
        using var outbound = ConnectorResumableProtocol.CreateRequest(plan, step, sessionLocation, offset, chunk);
        var uri = outbound.RequestUri!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var connection = await db.PluginConnections.AsNoTracking().Include(x => x.AgentInstallation!).ThenInclude(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == plan.ConnectionId && x.AgentInstallationId == plan.ConnectorInstallationId &&
                x.AgentInstallation!.BusinessId == plan.OrganizationId.ToString("D") && x.Status == PluginConnectionStatus.Connected && x.AgentInstallation.IsEnabled,
                timeout.Token) ?? throw new UnauthorizedAccessException("The media connection is unavailable.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(connection.AgentInstallation!.PackageVersion!.ManifestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var declaration = manifest.Connections.Single(x => x.Id == connection.DeclarationId);
        if (plan.Request.Connection != declaration.Id || connection.ProviderProfile != declaration.ProviderProfile ||
            connection.BoundResourceId != plan.ResourceId || !OutboundNetworkPolicy.IsAllowedOrigin(uri, declaration.AllowedOrigins))
            throw new UnauthorizedAccessException("The media destination is not approved for these credentials.");
        var blocked = OutboundNetworkPolicy.ParseCidrs(await db.AgentRuntimeGlobalSettings.AsNoTracking()
            .Select(x => x.BlockedNetworkCidrs).SingleOrDefaultAsync(timeout.Token));
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, timeout.Token);
        if (addresses.Length == 0 || addresses.Any(x => OutboundNetworkPolicy.IsForbiddenAddress(x, blocked)))
            throw new UnauthorizedAccessException("The media destination resolves to a blocked address.");
        var address = addresses[0];
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, uri.Port), token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await revalidate(timeout.Token);
        var access = await tokens.GetAccessTokenAsync(plan.ConnectorInstallationId, connection, timeout.Token)
            ?? throw new UnauthorizedAccessException("The media connection requires reconnection.");
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        await revalidate(timeout.Token);
        using var response = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var code = (int)response.StatusCode;
        var retry = ConnectorResumableProtocol.ParseRetryAfter(response.Headers, DateTimeOffset.UtcNow);
        if (step == ConnectorMediaStep.Begin && code is 200 or 201)
        {
            if (!response.Headers.TryGetValues("Location", out var locations))
                throw new InvalidOperationException("The provider did not return an upload session.");
            var values = locations.Take(2).ToArray();
            if (values.Length != 1) throw new InvalidOperationException("The provider returned ambiguous upload sessions.");
            var session = ConnectorResumableProtocol.ValidateSession(plan, values[0]);
            return new(code, [], session.AbsoluteUri, null, retry);
        }
        if (step != ConnectorMediaStep.Begin && code == 308)
            return new(code, [], null, ConnectorResumableProtocol.ParseCommittedBytes(
                response.Headers.TryGetValues("Range", out var ranges) ? ranges : [], plan.Media!.SizeBytes, maximumSentBytes), retry);
        if (code is not (200 or 201)) return new(code, [], null, null, retry); // Never propagate provider error bodies.
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("The upload result exceeded the response limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var result = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            if (result.Length + count > MaximumResponseBytes) throw new InvalidOperationException("The upload result exceeded the response limit.");
            result.Write(buffer, 0, count);
        }
        return new(code, result.ToArray(), null, null, retry);
    }
}
