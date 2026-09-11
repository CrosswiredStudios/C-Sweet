using System.Text;
using System.Threading.RateLimiting;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Core;

/// <summary>Terminates the entire preview-origin request pipeline before Headquarters authentication/routing.
/// Product content can never reach a Headquarters endpoint through the preview host.</summary>
public sealed partial class WebPreviewGatewayMiddleware(RequestDelegate next, WebPreviewOrigins origins,
    IOptions<WebPreviewGatewayOptions> options)
{
    private readonly ConcurrencyLimiter requests = new(new ConcurrencyLimiterOptions
        { PermitLimit = 16, QueueLimit = 64, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
    private static readonly HashSet<string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Accept", "Accept-Language", "Content-Type", "Content-Encoding", "Range", "If-None-Match", "If-Modified-Since" };
    private static readonly HashSet<string> ResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Content-Type", "Content-Encoding", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified" };

    public async Task InvokeAsync(HttpContext context, IWebPreviewGateway gateway)
    {
        if (!origins.IsPreviewHost(context.Request.Host.Host)) { await next(context); return; }
        SetBoundaryHeaders(context.Response);
        if (!context.Request.IsHttps || context.Request.Host.Port is not (null or 443)) { context.Response.StatusCode = 400; return; }
        using var lease = await requests.AcquireAsync(1, context.RequestAborted);
        if (!lease.IsAcquired) { context.Response.StatusCode = 503; return; }
        try
        {
            var preview = origins.PreviewId(context.Request.Host.Host);
            var origin = origins.Origin(preview);
            if (context.Request.Path == "/__preview/session")
            {
                if (context.Request.Method != "POST" || context.Request.Headers.Origin != options.Value.HeadquartersOrigin.TrimEnd('/') ||
                    context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true)
                    throw new UnauthorizedAccessException();
                var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(await ReadBodyAsync(context.Request, 2048, context.RequestAborted)));
                if (form.Count != 1 || !form.TryGetValue("ticket", out var ticket) || ticket.Count != 1) throw new UnauthorizedAccessException();
                var session = await gateway.RedeemAsync(preview, ticket.ToString(), context.RequestAborted);
                context.Response.Cookies.Append(WebPreviewOrigins.CookieName, session.Value, new()
                { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = session.ExpiresAt });
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>Opening preview</title><script>location.replace('/')</script>", context.RequestAborted);
                return;
            }

            var requestOrigin = context.Request.Headers.Origin.ToString();
            if (requestOrigin.Length > 0 && requestOrigin != origin ||
                context.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site" ||
                context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && requestOrigin != origin)
                throw new UnauthorizedAccessException();
            if (!context.Request.Cookies.TryGetValue(WebPreviewOrigins.CookieName, out var cookie)) throw new UnauthorizedAccessException();
            if (context.Request.Path.StartsWithSegments("/__preview"))
            {
                await gateway.ValidateSessionAsync(preview, cookie, context.RequestAborted);
                if (context.Request.Path == "/__preview/client.js" && context.Request.Method == "GET")
                {
                    context.Response.ContentType = "text/javascript; charset=utf-8";
                    await context.Response.WriteAsync(ClientDiagnosticScript, context.RequestAborted); return;
                }
                if (context.Request.Path == "/__preview/diagnostics" && context.Request.Method == "POST")
                {
                    var input = System.Text.Json.JsonSerializer.Deserialize<PreviewClientEvent>(await ReadBodyAsync(context.Request, 8192, context.RequestAborted), CSweet.WebHost.Contracts.PreviewJson.Options)
                        ?? throw new InvalidDataException();
                    await gateway.RecordClientEventAsync(preview, cookie, input, context.RequestAborted); context.Response.StatusCode = 204; return;
                }
                context.Response.StatusCode = 404; return;
            }
            if (context.WebSockets.IsWebSocketRequest)
            { await ForwardSocketAsync(context, gateway, preview, cookie); return; }
            var headers = context.Request.Headers.Where(x => RequestHeaders.Contains(x.Key))
                .ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var body = await ReadBodyAsync(context.Request, ProductGuestProtocol.MaximumHttpBodyBytes, context.RequestAborted);
            await ForwardAsync(context, gateway, preview, cookie, headers, body);
        }
        catch (UnauthorizedAccessException) { if (!context.Response.HasStarted) context.Response.StatusCode = 403; else context.Abort(); }
        catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 504; else context.Abort(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or System.Text.Json.JsonException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; else context.Abort(); }
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpRequest request, int maximum, CancellationToken token)
    {
        if (request.ContentLength > maximum) throw new InvalidDataException("The preview request exceeds its input limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var result = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, deadline.Token); if (count == 0) break;
            if (result.Length + count > maximum) throw new InvalidDataException("The preview request exceeds its input limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    private static void SetBoundaryHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; " +
            "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; " +
            "worker-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'; form-action 'self'";
    }
    private const string ClientDiagnosticScript = """
(() => {
  let sent = 0;
  const report = (code, value) => {
    if (sent++ >= 20) return;
    const summary = String(value || 'A browser error occurred.').slice(0, 2048);
    fetch('/__preview/diagnostics', { method: 'POST', credentials: 'same-origin', headers: {'Content-Type':'application/json'},
      body: JSON.stringify({code, summary}), keepalive: true }).catch(() => {});
  };
  addEventListener('error', event => report('BrowserError', event.message || 'A product asset failed to load.'), true);
  addEventListener('unhandledrejection', event => report('UnhandledRejection', event.reason instanceof Error ? event.reason.message : 'A promise was rejected.'));
})();
""";}
