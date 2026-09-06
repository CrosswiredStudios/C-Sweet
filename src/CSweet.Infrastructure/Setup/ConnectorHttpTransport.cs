using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed record ConnectorProviderResponse(int StatusCode, byte[] Body);

/// <summary>Trusted host transport only. Neither this API nor its responses are exposed as raw runtime tools.</summary>
public interface IConnectorHttpTransport
{
    Task<ConnectorProviderResponse> SendAsync(Guid connectorId, Guid connectionId, ConnectorPreparedRequest request,
        Func<CancellationToken, Task> revalidate, CancellationToken token);
}

public sealed class ConnectorHttpTransport(CSweetDbContext db, IPluginOAuthTokenBroker tokens) : IConnectorHttpTransport
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    public async Task<ConnectorProviderResponse> SendAsync(Guid connectorId, Guid connectionId, ConnectorPreparedRequest request,
        Func<CancellationToken, Task> revalidate, CancellationToken token)
    {
        if (request.MediaAssetId is not null) throw new InvalidOperationException("Media requires the durable transfer broker.");
        var uri = new Uri(request.Url, UriKind.Absolute);
        if (uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UnauthorizedAccessException("The prepared destination is invalid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var connection = await db.PluginConnections.AsNoTracking().Include(x => x.AgentInstallation!).ThenInclude(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == connectionId && x.AgentInstallationId == connectorId &&
                x.Status == PluginConnectionStatus.Connected && x.AgentInstallation!.IsEnabled, timeout.Token)
            ?? throw new UnauthorizedAccessException("The provider connection is unavailable.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(connection.AgentInstallation!.PackageVersion!.ManifestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var declaration = manifest.Connections.Single(x => x.Id == connection.DeclarationId);
        if (request.Connection != declaration.Id || connection.ProviderProfile != declaration.ProviderProfile ||
            !OutboundNetworkPolicy.IsAllowedOrigin(uri, declaration.AllowedOrigins))
            throw new UnauthorizedAccessException("Credentials are not approved for this prepared destination.");
        var blocked = OutboundNetworkPolicy.ParseCidrs(await db.AgentRuntimeGlobalSettings.AsNoTracking()
            .Select(x => x.BlockedNetworkCidrs).SingleOrDefaultAsync(timeout.Token));
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, timeout.Token);
        if (addresses.Length == 0 || addresses.Any(x => OutboundNetworkPolicy.IsForbiddenAddress(x, blocked)))
            throw new UnauthorizedAccessException("The provider resolves to a blocked address.");
        var address = addresses[0];
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (_, cancel) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, uri.Port), cancel); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await revalidate(timeout.Token);
        var accessToken = await tokens.GetAccessTokenAsync(connectorId, connection, timeout.Token)
            ?? throw new UnauthorizedAccessException("The provider requires reconnection.");
        using var outbound = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        outbound.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (request.Body is not null) outbound.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
        await revalidate(timeout.Token); // Refresh/DNS must not extend revoked authority.
        using var response = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("Provider redirects are not part of the approved request.");
        // Do not propagate provider error bodies: they may echo credentials, upload URLs or external instructions.
        if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, []);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("The provider response exceeded the safe size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            if (output.Length + count > MaximumResponseBytes)
                throw new InvalidOperationException("The provider response exceeded the safe size limit.");
            output.Write(buffer, 0, count);
        }
        return new((int)response.StatusCode, output.ToArray());
    }
}
