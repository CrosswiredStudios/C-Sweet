using System.Net;
using System.Net.Http.Headers;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class RepositoryMetadataCacheTests
{
    [Fact]
    public async Task MetadataCache_CoalescesReadsAndPartitionsCredentials()
    {
        using var cache = new RepositoryMetadataCache();
        var inner = new CountingHandler();
        using var client = Client(cache, inner);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetStringAsync("repos/owner/repo")));
        Assert.All(responses, x => Assert.Equal("{}", x)); Assert.Equal(1, inner.Calls);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "different-test-credential");
        await client.GetStringAsync("repos/owner/repo"); Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task MetadataCache_StopsRequestsAcrossRepositoriesUntilProviderReset()
    {
        using var cache = new RepositoryMetadataCache();
        var inner = new CountingHandler { RateLimited = true };
        using var client = Client(cache, inner);
        using var response = await client.GetAsync("repos/owner/repo");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var error = await Assert.ThrowsAsync<AgentImportPreviewException>(() => client.GetAsync("repos/another/project"));
        Assert.True(error.IsRateLimited); Assert.NotNull(error.RetryAt); Assert.Equal(1, inner.Calls);
        // Changing credentials does not reuse anonymous failures or metadata.
        inner.RateLimited = false;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-credential");
        await client.GetStringAsync("repos/another/project"); Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task MetadataCache_DoesNotCacheProviderFailuresOrReleaseDownloads()
    {
        using var cache = new RepositoryMetadataCache();
        var inner = new CountingHandler { Failing = true };
        using var client = Client(cache, inner);
        using var a = await client.GetAsync("repos/owner/repo");
        using var b = await client.GetAsync("repos/owner/repo");
        inner.Failing = false;
        await client.GetAsync("repos/owner/repo/releases/assets/123");
        await client.GetAsync("repos/owner/repo/releases/assets/123");
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task MetadataCache_RevalidatesMutableFactsWithEtagAfterExpiry()
    {
        using var cache = new RepositoryMetadataCache();
        var clock = new MetadataClock(); var inner = new CountingHandler();
        using var client = new HttpClient(new RepositoryMetadataCacheHandler(cache, clock) { InnerHandler = inner })
            { BaseAddress = new("https://api.github.com/") };
        Assert.Equal("{}", await client.GetStringAsync("repos/owner/repo/commits/main"));
        clock.Now = clock.Now.AddMinutes(2); inner.NotModified = true;
        Assert.Equal("{}", await client.GetStringAsync("repos/owner/repo/commits/main"));
        Assert.Equal("\"metadata-v1\"", inner.IfNoneMatch);
        Assert.Equal("{}", await client.GetStringAsync("repos/owner/repo/commits/main"));
        Assert.Equal(2, inner.Calls);
    }

    private sealed class MetadataClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    private static HttpClient Client(RepositoryMetadataCache cache, HttpMessageHandler inner) => new(
        new RepositoryMetadataCacheHandler(cache, TimeProvider.System) { InnerHandler = inner }) { BaseAddress = new("https://api.github.com/") };
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls; public bool RateLimited; public bool Failing; public bool NotModified; public string? IfNoneMatch;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            IfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            var response = new HttpResponseMessage(RateLimited ? HttpStatusCode.TooManyRequests : Failing ? HttpStatusCode.BadGateway : NotModified ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            { Content = new StringContent("{}") };
            response.Headers.ETag = new EntityTagHeaderValue("\"metadata-v1\"");
            if (RateLimited) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        }
    }
}
