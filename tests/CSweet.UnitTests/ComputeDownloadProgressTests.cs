using CSweet.Api.Compute;
using CSweet.Contracts.Compute;
using System.Net;

namespace CSweet.UnitTests;

public sealed class ComputeDownloadProgressTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-13T05:00:00Z");
    private static ComputeSetupStatus Status(long bytes) => new("Running", "Downloading", Start, bytes, null);

    [Fact]
    public void EstimatesUseObservedGrowthAndClearWhenStalledRestartedOrFinished()
    {
        var tracker = new ComputeDownloadProgress(new Factory(), TimeProvider.System);
        var id = Guid.NewGuid();
        Assert.Null(tracker.Observe(id, Start, "image", Status(100), 1000, Start).DownloadSecondsRemaining);
        var moving = tracker.Observe(id, Start, "image", Status(300), 1000, Start.AddSeconds(10));
        Assert.Equal(20d, moving.DownloadBytesPerSecond);
        Assert.Equal(35d, moving.DownloadSecondsRemaining);
        var stalled = tracker.Observe(id, Start, "image", Status(300), 1000, Start.AddSeconds(45));
        Assert.Null(stalled.DownloadSecondsRemaining); Assert.True(stalled.DownloadWaitingForProgress);
        var resumed = tracker.Observe(id, Start, "image", Status(500), 1000, Start.AddSeconds(50));
        Assert.NotNull(resumed.DownloadSecondsRemaining); Assert.False(resumed.DownloadWaitingForProgress);
        var reset = tracker.Observe(id, Start, "image", Status(10), 1000, Start.AddSeconds(60));
        Assert.Null(reset.DownloadSecondsRemaining);
        var complete = tracker.Observe(id, Start, "image", Status(1000), 1000, Start.AddSeconds(70));
        Assert.Equal(0d, complete.DownloadSecondsRemaining); Assert.Null(complete.DownloadBytesPerSecond);
        Assert.Null(tracker.Observe(id, Start.AddMinutes(2), "image", Status(100), 1000, Start.AddMinutes(2)).DownloadSecondsRemaining);
    }

    [Fact]
    public async Task TotalComesFromCachedOfficialHeadersAndUntrustedFilenamesNeverMakeRequests()
    {
        var factory = new Factory();
        var tracker = new ComputeDownloadProgress(factory, TimeProvider.System);
        var id = Guid.NewGuid();
        var first = await tracker.EnrichAsync(id, Start, "ubuntu-24.04.4-live-server-amd64.iso", Status(100), default);
        Assert.Equal(1000L, first.TotalDownloadBytes);
        await tracker.EnrichAsync(id, Start, "ubuntu-24.04.4-live-server-amd64.iso", Status(200), default);
        Assert.Equal(1, factory.Handler.Calls);
        var invalid = await tracker.EnrichAsync(id, Start, "../../private.iso", Status(200), default);
        Assert.Null(invalid.TotalDownloadBytes); Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public void UnknownOrInconsistentTotalDoesNotFabricateAnEta()
    {
        var tracker = new ComputeDownloadProgress(new Factory(), TimeProvider.System);
        var id = Guid.NewGuid();
        tracker.Observe(id, Start, "image", Status(100), null, Start);
        var unknown = tracker.Observe(id, Start, "image", Status(300), null, Start.AddSeconds(10));
        Assert.NotNull(unknown.DownloadBytesPerSecond); Assert.Null(unknown.DownloadSecondsRemaining);
        var inconsistent = tracker.Observe(id, Start, "image", Status(500), 400, Start.AddSeconds(20));
        Assert.Null(inconsistent.TotalDownloadBytes); Assert.Null(inconsistent.DownloadSecondsRemaining);
    }

    private sealed class Factory : IHttpClientFactory
    {
        public Headers Handler { get; } = new();
        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false);
    }
    private sealed class Headers : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal("https://releases.ubuntu.com/24.04.4/ubuntu-24.04.4-live-server-amd64.iso", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = 1000;
            return Task.FromResult(response);
        }
    }
}
