using System.Net.WebSockets;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Core;
namespace CSweet.Api.Core;

public sealed partial class WebPreviewGatewayMiddleware
{
    private sealed record BrowserSocketMessage(byte[] Data, WebSocketMessageType Type);
    private static async Task<BrowserSocketMessage> ReadBrowserAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[65537]; var length = 0; ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (result.MessageType == WebSocketMessageType.Close) return new([], WebSocketMessageType.Close);
            length += result.Count;
            if (length > 65536) throw new InvalidDataException("The preview WebSocket message exceeds its bound.");
        } while (!result.EndOfMessage);
        return new(buffer.AsSpan(0, length).ToArray(), result.MessageType);
    }
    private static async Task ForwardSocketAsync(HttpContext context, IWebPreviewGateway gateway, Guid preview, string cookie)
    {
        var expectedOrigin = "https://" + context.Request.Host.Host;
        if (context.Request.Headers.Origin != expectedOrigin) throw new UnauthorizedAccessException();
        var id = Guid.NewGuid();
        var path = context.Request.Path + context.Request.QueryString;
        var cookies = context.Request.Cookies.Where(x => x.Key != WebPreviewOrigins.CookieName && ProductGuestProtocol.ValidCookie(x.Key, x.Value))
            .Take(32).ToDictionary(x => x.Key, x => x.Value);
        var protocols = context.WebSockets.WebSocketRequestedProtocols.ToArray();
        Task<GuestHttpResponse> Operation(string operation, byte[]? body = null) => gateway.SendAsync(preview, cookie,
            new("GET", path, new Dictionary<string,string>(), body ?? [], cookies, new(operation, id, operation == "open" ? protocols : null)), context.RequestAborted);
        var opened = await Operation("open");
        if (opened.Headers.GetValueOrDefault("X-CSweet-Socket-State") != "open") throw new InvalidDataException("The product WebSocket is unavailable.");
        var protocol = opened.Headers.GetValueOrDefault("X-CSweet-Socket-Protocol");
        if (!string.IsNullOrEmpty(protocol) && !protocols.Contains(protocol, StringComparer.Ordinal)) throw new InvalidDataException("The WebSocket protocol changed.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync(string.IsNullOrEmpty(protocol) ? null : protocol);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task<BrowserSocketMessage>? incoming = null;
        try
        {
            incoming = ReadBrowserAsync(socket, lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                // Keep one DB operation at a time per scope; waiting for the browser does not block guest reads.
                if (await Task.WhenAny(incoming, Task.Delay(200, lifetime.Token)) == incoming)
                {
                    var message = await incoming;
                    if (message.Type == WebSocketMessageType.Close) break;
                    await Operation(message.Type == WebSocketMessageType.Text ? "send-text" : "send-binary", message.Data);
                    incoming = ReadBrowserAsync(socket, lifetime.Token);
                }
                var received = await Operation("receive");
                switch (received.Headers.GetValueOrDefault("X-CSweet-Socket-State"))
                {
                    case "closed": return;
                    case "open": break;
                    case "message":
                    {
                        if (received.Body.Length > 65536 || received.Headers.GetValueOrDefault("X-CSweet-Socket-Type") is not ("text" or "binary"))
                            throw new InvalidDataException("The product WebSocket response exceeds its bound.");
                        using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); sendDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                        await socket.SendAsync(received.Body.AsMemory(), received.Headers["X-CSweet-Socket-Type"] == "text" ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                            true, sendDeadline.Token); break;
                    }
                    default: throw new InvalidDataException("The product WebSocket state is invalid.");
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { socket.Abort(); }
        finally
        {
            lifetime.Cancel(); socket.Abort();
            if (incoming is not null) try { await incoming; } catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            // A fresh close is cleanup only; abandoned guest connections also expire independently after two minutes.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await gateway.SendAsync(preview, cookie, new("GET", path, new Dictionary<string,string>(), [], cookies, new("close", id)), cleanup.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException) { }
        }
    }
}
