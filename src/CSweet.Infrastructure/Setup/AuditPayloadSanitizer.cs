using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CSweet.Infrastructure.Setup;

public sealed record AuditPayloadEvidence(
    string Sha256,
    long Size,
    string? Preview,
    bool Truncated,
    string? FullContent = null);

public static partial class AuditPayloadSanitizer
{
    public const int MaximumPreviewBytes = 64 * 1024;

    private static readonly string[] SecretTerms =
    [
        "token", "authorization", "password", "secret", "apikey", "cookie",
        "credential", "recoverycode", "privatekey", "protectedreasoning", "encryptedreasoning", "restrictedmemory", "protecteddata", "memorycontent"
    ];

    public static AuditPayloadEvidence Capture(ReadOnlyMemory<byte> payload, string? contentType)
    {
        var bytes = payload.Span;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length == 0)
            return new AuditPayloadEvidence(hash, 0, null, false);

        if (!IsJson(contentType))
        {
            if (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) != true)
                return new AuditPayloadEvidence(hash, bytes.Length, null, false);
            return Evidence(hash, bytes.Length, RedactText(Encoding.UTF8.GetString(bytes)));
        }

        try
        {
            var node = JsonNode.Parse(bytes);
            Redact(node);
            return Evidence(hash, bytes.Length, node?.ToJsonString() ?? "null");
        }
        catch (JsonException)
        {
            return new AuditPayloadEvidence(hash, bytes.Length, null, false);
        }
    }

    public static string? RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var node = JsonNode.Parse(json);
            Redact(node);
            return node?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AuditPayloadEvidence Evidence(string hash, long size, string content)
    {
        // Preview is presentation-only; the full sanitized value remains available.
        var truncated = Encoding.UTF8.GetByteCount(content) > MaximumPreviewBytes;
        var preview = content.Length > MaximumPreviewBytes / 4 ? content[..(MaximumPreviewBytes / 4)] : content;
        return new(hash, size, preview, truncated || preview.Length < content.Length, content);
    }

    public static string RedactText(string text)
    {
        var sanitized = AuthorizationValueRegex().Replace(text, "$1[REDACTED]");
        sanitized = BearerTokenRegex().Replace(sanitized, "$1[REDACTED]");
        return KeyValueSecretRegex().Replace(sanitized, match => match.Groups[1].Value + "[REDACTED]");
    }

    private static bool IsJson(string? contentType) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (IsSensitiveKey(property.Key)) obj[property.Key] = "[REDACTED]";
                    else Redact(property.Value);
                }
                break;
            case JsonArray array:
                // String redaction uses ReplaceWith, which changes the parent array's version.
                // Snapshot its children before visiting them, just as for object properties above.
                foreach (var child in array.ToArray()) Redact(child);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                {
                    try { var nested = JsonNode.Parse(text); Redact(nested); value.ReplaceWith(nested?.ToJsonString() ?? "null"); }
                    catch (JsonException) { value.ReplaceWith(RedactText(text)); }
                }
                else value.ReplaceWith(RedactText(text));
                break;
        }
    }

    public static bool IsSensitiveKey(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (normalized is "inputtokens" or "outputtokens" or "reasoningtokens" or "cachedtokens" or
            "tokencachedinputcount" or "tokeninputcount" or "tokenoutputcount" or "tokenreasoningcount" or
            "inputtokencount" or "outputtokencount" or "totaltokencount" or "maxoutputtokens") return false;
        return SecretTerms.Any(normalized.Contains);
    }
    [GeneratedRegex("(?i)(bearer\\s+)[a-z0-9._~+/=-]+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*)(?:bearer\\s+)?[a-z0-9._~+/=-]+")]
    private static partial Regex AuthorizationValueRegex();

    [GeneratedRegex("(?i)(\\\"?(?:api[_-]?key|password|secret|token|authorization|cookie|credential|protected[_-]?reasoning|encrypted[_-]?reasoning|restricted[_-]?memory|memory[_-]?content)\\\"?\\s*[:=]\\s*\\\"?)([^\\\"\\s,}]+)")]
    private static partial Regex KeyValueSecretRegex();
}
