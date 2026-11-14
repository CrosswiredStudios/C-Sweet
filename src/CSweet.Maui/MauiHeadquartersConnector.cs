using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using CSweet.Mobile.Transport;
using CSweet.UI.Mobile;
using Microsoft.AspNetCore.SignalR;

namespace CSweet.Maui;

/// <summary>
/// Opens a relay channel with the directory ticket, completes TLS pinned to the Headquarters key while presenting
/// this phone's device key, and exchanges hello/welcome. Closes the session afterwards (H4 transport proof).
/// </summary>
internal sealed class MauiHeadquartersConnector : IHeadquartersConnector
{
    public async Task<HeadquartersConnectResult> ConnectAsync(ConnectionTicket ticket, ECDsa deviceKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var certificate = PinnedTls.CreateCertificate(deviceKey, "C-Sweet phone", TimeSpan.FromDays(365));
        try
        {
            await using var session = await PhoneRelaySession.ConnectAsync(new SignalRRelayConnection(new Uri(ticket.RelayOrigin)),
                ticket.RouteId, ticket.Ticket, deviceKey, certificate, ticket.HeadquartersPin, timeout.Token);
            await HeadquartersHello.SendHelloAsync(session.Stream, timeout.Token);
            var welcome = await HeadquartersHello.ReadWelcomeAsync(session.Stream, timeout.Token);
            return new HeadquartersConnectResult(HeadquartersConnectStatus.Connected, session.Protocol.ToString(), welcome.Headquarters,
                welcome.Enrollment == HeadquartersHello.EnrollmentRequired);
        }
        catch (AuthenticationException)
        {
            return new HeadquartersConnectResult(HeadquartersConnectStatus.SecureConnectionFailed);
        }
        catch (HubException)
        {
            return new HeadquartersConnectResult(HeadquartersConnectStatus.Refused);
        }
        catch (Exception error) when (error is HttpRequestException or WebSocketException or IOException or InvalidDataException or OperationCanceledException)
        {
            return new HeadquartersConnectResult(HeadquartersConnectStatus.Unreachable);
        }
    }
}
