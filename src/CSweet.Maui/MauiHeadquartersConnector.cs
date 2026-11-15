using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using CSweet.Mobile.Transport;
using CSweet.UI.Mobile;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Maui.Devices;

namespace CSweet.Maui;

/// <summary>
/// Opens a relay channel with the directory ticket, completes TLS pinned to the Headquarters key while presenting
/// this phone's device key, and exchanges hello/welcome. An approved phone keeps the session open (TS-13): the
/// business list is its first read, and the enterprise app uses the same session afterwards.
/// </summary>
internal sealed class MauiHeadquartersConnector(HeadquartersTunnels tunnels) : IHeadquartersConnector
{
    private static string DeviceLabel()
    {
        var label = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
        return string.IsNullOrWhiteSpace(label) ? "Phone" : label;
    }

    private static IReadOnlyList<string> BusinessNames(byte[] json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array ? [] : document.RootElement.EnumerateArray()
            .Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null)
            .OfType<string>()
            .ToList();
    }

    public async Task<HeadquartersConnectResult> ConnectAsync(Guid installationId, ConnectionTicket ticket, ECDsa deviceKey, CancellationToken ct)
    {
        await tunnels.CloseAsync(installationId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var certificate = PinnedTls.CreateCertificate(deviceKey, "C-Sweet phone", TimeSpan.FromDays(365));
        PhoneRelaySession? session = null;
        try
        {
            session = await PhoneRelaySession.ConnectAsync(new SignalRRelayConnection(new Uri(ticket.RelayOrigin)),
                ticket.RouteId, ticket.Ticket, deviceKey, certificate, ticket.HeadquartersPin, timeout.Token);
            await HeadquartersHello.SendHelloAsync(session.Stream, ticket.DeviceAssertion ?? string.Empty, DeviceLabel(), timeout.Token);
            var welcome = await HeadquartersHello.ReadWelcomeAsync(session.Stream, timeout.Token);
            IReadOnlyList<string>? businesses = null;
            var protocol = session.Protocol.ToString();
            if (welcome.Enrollment == HeadquartersHello.EnrollmentActive)
            {
                var client = new PhoneTunnelClient(session.Stream);
                client.Start();
                // First live read over the tunnel: the business list, through Headquarters' own API pipeline.
                var reply = await client.SendAsync("GET", "/api/core/organizations", 1024 * 1024, timeout.Token);
                businesses = reply.Status == 200 ? BusinessNames(reply.Body) : [];
                tunnels.Keep(installationId, welcome.Headquarters, session, client, certificate);
                session = null; // owned by the tunnel registry now
                certificate = null;
            }
            return new HeadquartersConnectResult(HeadquartersConnectStatus.Connected, protocol, welcome.Headquarters,
                welcome.Enrollment, businesses);
        }
        catch (AuthenticationException)
        {
            return new HeadquartersConnectResult(HeadquartersConnectStatus.SecureConnectionFailed);
        }
        catch (HubException)
        {
            return new HeadquartersConnectResult(HeadquartersConnectStatus.Refused);
        }
        catch (Exception error) when (error is HttpRequestException or WebSocketException or IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new HeadquartersConnectResult(HeadquartersConnectStatus.Unreachable);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            certificate?.Dispose();
        }
    }
}

/// <summary>Open Headquarters sessions on this phone, one per enterprise.</summary>
public sealed class HeadquartersTunnels
{
    private readonly Dictionary<Guid, Live> _live = [];
    private readonly Lock _gate = new();

    private sealed record Live(string? Name, PhoneRelaySession Session, PhoneTunnelClient Client, IDisposable Certificate);

    internal void Keep(Guid installationId, string? name, PhoneRelaySession session, PhoneTunnelClient client, IDisposable certificate)
    {
        Live? previous;
        lock (_gate)
        {
            _live.TryGetValue(installationId, out previous);
            _live[installationId] = new Live(name, session, client, certificate);
        }
        if (previous is not null) _ = DisposeAsync(previous);
    }

    /// <summary>The enterprise's session, if it is still open.</summary>
    public PhoneTunnelClient? Get(Guid installationId)
    {
        lock (_gate) return _live.TryGetValue(installationId, out var live) && live.Client.EndedReason is null ? live.Client : null;
    }

    public async Task CloseAsync(Guid installationId)
    {
        Live? live;
        lock (_gate)
        {
            if (!_live.Remove(installationId, out live)) return;
        }
        await DisposeAsync(live);
    }

    private static async Task DisposeAsync(Live live)
    {
        try
        {
            await live.Client.DisposeAsync();
            await live.Session.DisposeAsync();
        }
        catch (Exception) { /* closing a broken session */ }
        live.Certificate.Dispose();
    }
}
