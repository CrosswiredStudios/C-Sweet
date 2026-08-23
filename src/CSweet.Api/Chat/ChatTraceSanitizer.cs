using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CSweet.Api.Chat;

internal static partial class ChatTraceSanitizer
{
    private const string Redacted = "[REDACTED]";
    private const int MaximumStringLength = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitiveKeys =
    [
        "authorization", "api-key", "apikey", "api_key", "password", "secret",
        "token", "cookie", "credential", "protecteddata", "protected_data",
        "protectedreasoning", "protected_reasoning", "encryptedreasoning", "encrypted_reasoning",
        "restrictedmemory", "restricted_memory", "memorycontent", "memory_content"
    ];

    public static string SanitizeText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sanitized = AuthorizationValueRegex().Replace(value, "$1" + Redacted);
        sanitized = BearerTokenRegex().Replace(sanitized, "$1" + Redacted);
        sanitized = KeyValueSecretRegex().Replace(sanitized, match =>
            match.Groups[1].Value + Redacted);
        return sanitized.Length <= MaximumStringLength
            ? sanitized
            : sanitized[..MaximumStringLength] + "…[TRUNCATED]";
    }

    public static object? SanitizeDetails(object? details)
    {
        if (details is null) return null;
        try
        {
            var node = JsonSerializer.SerializeToNode(details, JsonOptions);
            SanitizeNode(node);
            return node;
        }
        catch (JsonException)
        {
            return Redacted;
        }
        catch (NotSupportedException)
        {
            return Redacted;
        }
    }

    public static IReadOnlyDictionary<string, string>? SanitizeMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        return metadata.ToDictionary(
            entry => entry.Key,
            entry => IsSensitiveKey(entry.Key) ? Redacted : SanitizeText(entry.Value),
            StringComparer.Ordinal);
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeys.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveKey(property.Key))
                    obj[property.Key] = Redacted;
                else
                    SanitizeNode(property.Value);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array) SanitizeNode(item);
            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            value.ReplaceWith(SanitizeText(text));
    }

    [GeneratedRegex("(?i)(bearer\\s+)[a-z0-9._~+/=-]+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*)(?:bearer\\s+)?[a-z0-9._~+/=-]+")]
    private static partial Regex AuthorizationValueRegex();

    [GeneratedRegex("(?i)(\\\"?(?:api[_-]?key|password|secret|token|authorization|cookie|credential|protected[_-]?reasoning|encrypted[_-]?reasoning|restricted[_-]?memory|memory[_-]?content)\\\"?\\s*[:=]\\s*\\\"?)([^\\\"\\s,}]+)")]
    private static partial Regex KeyValueSecretRegex();
}
