using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace CSweet.Infrastructure.Setup;

/// <summary>Bounded, credential-partitioned metadata reuse and provider-directed cooldown.
/// This caches public repository facts, never approval, grants or execution authority.</summary>
public sealed class RepositoryMetadataCache : IDisposable
{
    internal readonly MemoryCache Entries = new(new MemoryCacheOptions { SizeLimit = 256 });
    internal readonly SemaphoreSlim Gate = new(1, 1);
    public void Dispose() { Entries.Dispose(); Gate.Dispose(); }
}

public sealed class RepositoryMetadataCacheHandler(RepositoryMetadataCache cache, TimeProvider clock) : DelegatingHandler
{
    private sealed record Entry(HttpStatusCode Status, byte[] Body, Dictionary<string, string[]> Headers,
        Dictionary<string, string[]> ContentHeaders, DateTimeOffset FreshUntil);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { Scheme: "https", Host: "api.github.com" } uri ||
            !uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/releases/assets/", StringComparison.Ordinal))
            return await base.SendAsync(request, ct);
        var identity = request.Headers.Authorization?.ToString() ?? "anonymous";
        var partition = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var key = partition + ":" + uri.AbsoluteUri;
        var cooldownKey = partition + ":cooldown";
        await cache.Gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            cache.Entries.TryGetValue<Entry>(key, out var previous);
            if (previous is not null && previous.FreshUntil > now) return Response(previous, request);
            if (cache.Entries.TryGetValue<DateTimeOffset>(cooldownKey, out var until) && until > now)
                throw new CSweet.Application.Setup.AgentImportPreviewException($"GitHub temporarily rate-limited repository access. Retry after {until.UtcDateTime:u}.")
                { IsDependencyFailure = true, IsRateLimited = true, RetryAt = until };
            if (previous?.Headers.TryGetValue("ETag", out var etags) == true)
                request.Headers.TryAddWithoutValidation("If-None-Match", etags);
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden &&
                (response.StatusCode == HttpStatusCode.TooManyRequests || response.Headers.RetryAfter is not null ||
                 response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
            {
                var reset = response.Headers.RetryAfter?.Date ?? (response.Headers.RetryAfter?.Delta is { } delta ? now.Add(delta) : (DateTimeOffset?)null);
                if (reset is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var seconds) && seconds is >= 0 and <= 253402300799)
                    reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                reset = reset > now ? reset : now.AddMinutes(1);
                cache.Entries.Set(cooldownKey, reset.Value, new MemoryCacheEntryOptions { AbsoluteExpiration = reset, Size = 1 });
                return response;
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var immutableCommit = segments.Length == 5 && segments[3] == "commits" && IsCommit(segments[4]);
            var immutableFile = segments.Length >= 5 && segments[3] == "contents" && uri.Query.TrimStart('?').Split('&')
                .Any(p => p.StartsWith("ref=", StringComparison.Ordinal) && IsCommit(Uri.UnescapeDataString(p[4..])));
            var lifetime = immutableCommit || immutableFile
                ? TimeSpan.FromHours(24) : uri.AbsolutePath.Contains("/releases/tags/", StringComparison.Ordinal)
                    ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);
            if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
            {
                response.Dispose();
                var refreshed = previous with { FreshUntil = now.Add(lifetime) };
                cache.Entries.Set(key, refreshed, new MemoryCacheEntryOptions { AbsoluteExpiration = now.AddDays(1), Size = 1 });
                return Response(refreshed, request);
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024) return response;
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            if (body.Length <= 1024 * 1024)
            {
                var entry = new Entry(response.StatusCode, body, response.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                    response.Content.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase), now.Add(lifetime));
                cache.Entries.Set(key, entry, new MemoryCacheEntryOptions { AbsoluteExpiration = now.AddDays(1), Size = 1 });
            }
            return response;
        }
        finally { cache.Gate.Release(); }
    }

    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static HttpResponseMessage Response(Entry entry, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(entry.Status) { RequestMessage = request, Content = new ByteArrayContent(entry.Body) };
        foreach (var header in entry.Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in entry.ContentHeaders) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }
}
