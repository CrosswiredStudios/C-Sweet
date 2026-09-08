using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace CSweet.Infrastructure.Setup;

public enum ConnectorMediaStep { Begin, Probe, Chunk }

/// <summary>Host-only transfer response. Session locations must never enter runtime results or audit text.</summary>
public sealed record ConnectorMediaResponse(int StatusCode, byte[] Body, string? SessionLocation,
    long? CommittedBytes, DateTimeOffset? RetryAfter);

/// <summary>Fixed HTTP range protocol. All metadata comes from the frozen plan; no runtime substitutions.</summary>
public static class ConnectorResumableProtocol
{
    public const string Name = "resumable-range.v1";
    public const int ChunkSize = 8 * 1024 * 1024;
    public const int Alignment = 256 * 1024;
    public const long MaximumBytes = 256L * 1024 * 1024 * 1024;

    public static Uri ValidateSession(FrozenConnectorPlan plan, string location)
    {
        ValidatePlan(plan);
        var initiation = new Uri(plan.Request.Url, UriKind.Absolute);
        if (location.Length > 16384 || location.Any(char.IsControl) ||
            !Uri.TryCreate(location, UriKind.Absolute, out var session) ||
            session.Scheme != "https" || !session.IsDefaultPort || session.IsLoopback ||
            !string.IsNullOrEmpty(session.UserInfo) || !string.IsNullOrEmpty(session.Fragment) ||
            session.GetLeftPart(UriPartial.Authority) != initiation.GetLeftPart(UriPartial.Authority) ||
            session.AbsolutePath != initiation.AbsolutePath)
            throw new UnauthorizedAccessException("The upload session is outside the approved initiation destination.");
        return session;
    }

    public static HttpRequestMessage CreateRequest(FrozenConnectorPlan plan, ConnectorMediaStep step,
        string? sessionLocation = null, long offset = 0, ReadOnlyMemory<byte> chunk = default)
    {
        ValidatePlan(plan);
        var media = plan.Media!;
        if (step == ConnectorMediaStep.Begin)
        {
            if (sessionLocation is not null || offset != 0 || !chunk.IsEmpty)
                throw new InvalidOperationException("Initiation cannot substitute a session or media range.");
            var request = new HttpRequestMessage(HttpMethod.Post, plan.Request.Url)
                { Content = new StringContent(plan.Request.Body ?? "{}", Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Upload-Content-Length", media.SizeBytes.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-Upload-Content-Type", media.ContentType);
            return request;
        }
        if (sessionLocation is null) throw new InvalidOperationException("An opaque saved upload session is required.");
        var session = ValidateSession(plan, sessionLocation);
        if (step == ConnectorMediaStep.Probe)
        {
            if (offset != 0 || !chunk.IsEmpty) throw new InvalidOperationException("Status probes cannot contain media bytes.");
            var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(media.SizeBytes);
            return request;
        }
        if (step != ConnectorMediaStep.Chunk || chunk.Length is < 1 or > ChunkSize || offset < 0 ||
            offset >= media.SizeBytes || chunk.Length > media.SizeBytes - offset ||
            offset + chunk.Length != media.SizeBytes && chunk.Length % Alignment != 0)
            throw new InvalidOperationException("The media range is not valid for the approved asset.");
        var outbound = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ReadOnlyMemoryContent(chunk) };
        outbound.Content.Headers.ContentType = new MediaTypeHeaderValue(media.ContentType);
        outbound.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + chunk.Length - 1, media.SizeBytes);
        return outbound;
    }

    public static long ParseCommittedBytes(IEnumerable<string> ranges, long totalBytes, long maximumSentBytes)
    {
        if (totalBytes is < 1 or > MaximumBytes || maximumSentBytes < 0 || maximumSentBytes > totalBytes)
            throw new InvalidOperationException("The expected upload range is invalid.");
        var values = ranges.Take(2).ToArray();
        if (values.Length == 0) return 0; // The provider has acknowledged no bytes, not the whole attempted chunk.
        if (values.Length != 1 || !values[0].StartsWith("bytes=0-", StringComparison.Ordinal) ||
            !long.TryParse(values[0].AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            end < 0 || end >= totalBytes || end >= maximumSentBytes)
            throw new InvalidOperationException("The provider returned an invalid or impossible upload range.");
        return end + 1;
    }

    public static DateTimeOffset? ParseRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        // Bound retries without reducing a provider-requested delay. Excessive/malformed values block.
        if (!headers.TryGetValues("Retry-After", out var values)) return null;
        var raw = values.Take(2).ToArray();
        if (raw.Length != 1 || !RetryConditionHeaderValue.TryParse(raw[0], out var parsed))
            throw new InvalidOperationException("The provider returned an invalid retry delay.");
        var next = parsed.Date ?? now.Add(parsed.Delta ?? TimeSpan.Zero);
        if (next > now.AddDays(1)) throw new InvalidOperationException("The provider requires a manual retry review.");
        return next < now ? now : next;
    }

    private static void ValidatePlan(FrozenConnectorPlan plan)
    {
        if (plan.Request.MediaProtocol != Name || plan.Request.Method != "POST" || plan.Request.Effect == "read" ||
            plan.Media is not { SizeBytes: > 0 and <= MaximumBytes } media ||
            !Guid.TryParse(plan.Request.MediaAssetId, out var asset) || asset != media.AssetId ||
            !MediaTypeHeaderValue.TryParse(media.ContentType, out var type) || type.Parameters.Count != 0 ||
            media.Sha256.Length != 64 || !media.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The approved plan does not describe a supported, checksummed media transfer.");
        if (!Uri.TryCreate(plan.Request.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UnauthorizedAccessException("The approved media destination is invalid.");
    }
}
