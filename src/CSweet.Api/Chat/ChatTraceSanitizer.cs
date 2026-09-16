using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Infrastructure.Setup;

namespace CSweet.Api.Chat;

// Chat traces and the audit ledger share one redaction policy.
internal static class ChatTraceSanitizer
{
    public static string SanitizeText(string value)
    {
        var safe = AuditPayloadSanitizer.RedactText(value);
        return safe.Length <= 65_536 ? safe : safe[..65_536] + "…[TRUNCATED]";
    }
    public static object? SanitizeDetails(object? details)
    {
        if (details is null) return null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var safe = AuditPayloadSanitizer.Capture(bytes, "application/json").FullContent;
            return safe is null ? "[REDACTED]" : JsonNode.Parse(safe);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return "[REDACTED]"; }
    }
    public static IReadOnlyDictionary<string, string>? SanitizeMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata?.ToDictionary(x => x.Key, x => AuditPayloadSanitizer.IsSensitiveKey(x.Key) ? "[REDACTED]" : SanitizeText(x.Value), StringComparer.Ordinal);
}
