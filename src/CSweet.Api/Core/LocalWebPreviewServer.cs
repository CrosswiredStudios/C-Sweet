using System.Net;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CSweet.Api.Core;

/// <summary>A separate, expiring loopback origin serving only verified immutable web files.</summary>
internal sealed class LocalWebPreviewServer(WebApplication app, string accessReference) : IAsyncDisposable
{
    public string AccessReference { get; } = accessReference;

    internal static async Task<LocalWebPreviewServer> StartAsync(WebPreviewBundle bundle, DateTimeOffset expiresAt,
        TimeProvider clock, CancellationToken token)
    {
        if (expiresAt <= clock.GetUtcNow()) throw new InvalidOperationException("The preview has expired.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var prefix = "/" + Guid.NewGuid().ToString("N") + "/";
        app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "sandbox allow-scripts allow-pointer-lock; default-src 'none'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; " +
                "connect-src 'self'; worker-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            // Sandboxed documents have an opaque origin. Only immutable preview files permit CORS.
            context.Response.Headers.AccessControlAllowOrigin = "*";
            if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 400; return; }
            if (clock.GetUtcNow() >= expiresAt) { context.Response.StatusCode = 410; return; }
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            { context.Response.StatusCode = 405; return; }
            var requestPath = context.Request.Path.Value ?? "";
            if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
            var path = requestPath[prefix.Length..];
            if (path.Length == 0) path = "index.html";
            if (!WebPreviewBundle.ValidPath(path) || !bundle.Files.TryGetValue(path, out var file))
            { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = file.ContentType;
            context.Response.ContentLength = file.Content.LongLength;
            if (HttpMethods.IsGet(context.Request.Method)) await context.Response.Body.WriteAsync(file.Content, context.RequestAborted);
        });
        try
        {
            await app.StartAsync(token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, address + prefix);
        }
        catch { await app.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }
}
