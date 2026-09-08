using System.Net;
using System.Text;
using CSweet.Api.Core;
using CSweet.Infrastructure.WorkManagement;

namespace CSweet.UnitTests;

public sealed class LocalWebPreviewServerTests
{
    [Fact]
    public async Task ServesRealWebFilesOnLoopbackAndRejectsWritesUnknownFilesAndExpiredAccess()
    {
        var clock = new PreviewClock();
        var html = Encoding.UTF8.GetBytes("<canvas id='game'></canvas><script src='game.js'></script>");
        var bundle = new WebPreviewBundle(new Dictionary<string, WebPreviewFile> {
            ["index.html"] = new(html, "text/html"),
            ["game.js"] = new(Encoding.UTF8.GetBytes("document.querySelector('canvas').width=320;"), "text/javascript") });
        await using var server = await LocalWebPreviewServer.StartAsync(bundle, clock.GetUtcNow().AddMinutes(5), clock, CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var response = await client.GetAsync(server.AccessReference);
        Assert.Equal("127.0.0.1", new Uri(server.AccessReference).Host);
        Assert.Equal(html, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox allow-scripts", csp);
        Assert.DoesNotContain("allow-same-origin", csp);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("width=320", await client.GetStringAsync(server.AccessReference + "game.js"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(server.AccessReference + "provenance.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(server.AccessReference).GetLeftPart(UriPartial.Authority))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync(server.AccessReference, new StringContent("write"))).StatusCode);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync(server.AccessReference)).StatusCode);
    }

    private sealed class PreviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
