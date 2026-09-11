using CSweet.Api.Core;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
namespace CSweet.UnitTests;

public sealed class WebPreviewTransportTests
{
    private sealed class Gateway(Func<GuestHttpRequest, GuestHttpResponse> respond) : IWebPreviewGateway
    {
        public List<GuestHttpRequest> Requests { get; } = [];
        public Task<GuestHttpResponse> SendAsync(Guid preview, string cookie, GuestHttpRequest request, CancellationToken token)
        { Assert.Equal("browser-secret", cookie); Requests.Add(request with { Headers = request.Headers.ToDictionary(x => x.Key, x => x.Value) }); return Task.FromResult(respond(request)); }
        public Task<WebPreviewSessionCookie> RedeemAsync(Guid previewId, string ticket, CancellationToken token) => throw new NotSupportedException();
        public Task ValidateSessionAsync(Guid previewId, string cookie, CancellationToken token) => Task.CompletedTask;
        public Task RecordClientEventAsync(Guid previewId, string cookie, PreviewClientEvent input, CancellationToken token) => Task.CompletedTask;
    }
    private static (WebPreviewGatewayMiddleware Middleware, DefaultHttpContext Context) Request(string method = "GET")
    {
        var options = Options.Create(new WebPreviewGatewayOptions { HeadquartersOrigin = "https://hq.example.com", PreviewHostSuffix = "preview.example.net" });
        var middleware = new WebPreviewGatewayMiddleware(_ => throw new InvalidOperationException("HQ must not receive preview requests"), new(options), options);
        var context = new DefaultHttpContext(); context.Request.Method = method; context.Request.Scheme = "https";
        context.Request.Host = new($"{Guid.NewGuid():N}.preview.example.net"); context.Request.Path = "/game.wasm";
        context.Request.Headers.Cookie = "__Host-CSweetPreview=browser-secret; game=session-data";
        context.Request.Headers.Authorization = "Bearer must-not-forward";
        context.Response.Body = new MemoryStream(); return (middleware, context);
    }
    [Fact]
    public async Task Large_asset_streams_contiguous_verified_chunks_without_forwarding_gateway_credentials()
    {
        const int total = 6 * 1024 * 1024;
        var gateway = new Gateway(request =>
        {
            Assert.False(request.Headers.ContainsKey("Authorization")); Assert.False(request.Headers.ContainsKey("Cookie"));
            Assert.False(request.ProductCookies!.ContainsKey(WebPreviewOrigins.CookieName)); Assert.Equal("session-data", request.ProductCookies["game"]);
            var start = long.Parse(request.Headers["Range"].Split('=')[1].Split('-')[0]); var end = Math.Min(total - 1, start + ProductGuestProtocol.MaximumHttpBodyBytes - 1);
            return new(206, new Dictionary<string,string> { ["Content-Range"] = $"bytes {start}-{end}/{total}", ["ETag"] = "\"immutable\"", ["Content-Type"] = "application/wasm" }, new byte[end-start+1]);
        });
        var (middleware, context) = Request(); await middleware.InvokeAsync(context, gateway);
        Assert.Equal(200, context.Response.StatusCode); Assert.Equal(total, context.Response.Body.Length); Assert.Equal(total, context.Response.ContentLength);
        Assert.Equal(2, gateway.Requests.Count); Assert.Equal("bytes=4194304-6291455", gateway.Requests[1].Headers["Range"]);
        Assert.False(context.Response.Headers.ContainsKey("Content-Range"));
    }
    [Fact]
    public async Task Product_cookies_cannot_replace_gateway_identity_or_set_a_parent_domain()
    {
        var gateway = new Gateway(_ => new(200, new Dictionary<string,string>(), [],
            ["game=abc; Domain=hq.example.com; Path=/other; SameSite=None", "__Host-CSweetPreview=stolen; Path=/; Secure"]));
        var (middleware, context) = Request(); await middleware.InvokeAsync(context, gateway);
        var cookie = Assert.Single(context.Response.Headers.SetCookie);
        Assert.DoesNotContain("Domain", cookie!, StringComparison.OrdinalIgnoreCase); Assert.Contains("path=/", cookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie!, StringComparison.OrdinalIgnoreCase); Assert.Contains("samesite=strict", cookie!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stolen", cookie!);
    }
    [Fact]
    public async Task Complete_html_receives_browser_diagnostics_without_changing_partial_asset_responses()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Game</title></head><body>Demo</body></html>");
        var gateway = new Gateway(_ => new(206, new Dictionary<string,string> { ["Content-Type"] = "text/html; charset=utf-8", ["Content-Range"] = $"bytes 0-{html.Length-1}/{html.Length}" }, html));
        var (middleware, context) = Request(); context.Request.Path = "/"; await middleware.InvokeAsync(context, gateway);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("/__preview/client.js", System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }
}
