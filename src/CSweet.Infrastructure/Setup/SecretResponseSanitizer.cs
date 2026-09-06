using System.Text.Json;
using System.Text.Json.Nodes;

namespace CSweet.Infrastructure.Setup;

/// <summary>Removes every declared secret before producing any runtime-visible response.</summary>
public static class SecretResponseSanitizer
{
    public static async Task<byte[]> SanitizeAsync(byte[] body, IReadOnlyList<string> fields,
        Func<string, string, CancellationToken, Task<string>> vault, CancellationToken token)
    {
        if (fields.Count == 0) return body;
        JsonNode root;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            _ = ConnectorRequestMaterializer.Hash(document.RootElement); // Reject ambiguous duplicate properties.
            root = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })
                ?? throw new InvalidOperationException("A secret-bearing response must be a JSON document.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("A secret-bearing response was malformed and was withheld.");
        }
        var pending = new List<(JsonObject Parent, string Key, string Path, string Value)>();
        foreach (var field in fields.Distinct(StringComparer.Ordinal))
        {
            if (!field.StartsWith('/') || field.Length > 1024)
                throw new InvalidOperationException("A secret selector is invalid.");
            var parts = field.Split('/').Skip(1).Select(x => x.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (parts.Length is 0 or > 32 || parts.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException("A secret selector is invalid.");
            Collect(root, parts, 0, "", pending);
        }
        // Validate the complete shape before the first vault write; never return a partially redacted body.
        foreach (var item in pending.DistinctBy(x => x.Path))
        {
            token.ThrowIfCancellationRequested();
            var reference = await vault(item.Path, item.Value, token);
            item.Parent[item.Key] = new JsonObject { ["secretReference"] = reference };
        }
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static void Collect(JsonNode node, string[] parts, int index, string path,
        List<(JsonObject Parent, string Key, string Path, string Value)> output)
    {
        if (parts[index] == "*")
        {
            if (node is not JsonArray array || index == parts.Length - 1)
                throw new InvalidOperationException("A declared secret array has an unexpected shape.");
            for (var i = 0; i < array.Count; i++)
                if (array[i] is { } child) Collect(child, parts, index + 1, path + "/" + i, output);
            return;
        }
        if (node is JsonArray indexed)
        {
            if (!int.TryParse(parts[index], out var number) || number < 0 || index == parts.Length - 1)
                throw new InvalidOperationException("A declared secret array has an invalid selector.");
            if (number < indexed.Count && indexed[number] is { } child)
                Collect(child, parts, index + 1, path + "/" + number, output);
            return;
        }
        if (node is not JsonObject parent)
            throw new InvalidOperationException("A declared secret response has an unexpected shape.");
        if (!parent.TryGetPropertyValue(parts[index], out var value) || value is null) return;
        var nextPath = path + "/" + parts[index];
        if (index < parts.Length - 1) { Collect(value, parts, index + 1, nextPath, output); return; }
        var secret = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        if (!string.IsNullOrEmpty(secret)) output.Add((parent, parts[index], nextPath, secret));
    }
}
