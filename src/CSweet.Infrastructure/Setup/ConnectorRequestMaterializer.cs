using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Plugins;

namespace CSweet.Infrastructure.Setup;

public sealed record PreparedResourceCheck(ConnectorResourceCheck Declaration, string ResourceId);
public sealed record ConnectorPreparedRequest(string Method, string Url, string? Body, string Connection,
    string BoundResourceId, string Effect, IReadOnlyList<PreparedResourceCheck> ResourceChecks,
    string? MediaAssetId, IReadOnlyList<string> SecretResponseFields);

/// <summary>Materializes a closed manifest mapping. No plugin-supplied URLs or executable templates.</summary>
public static class ConnectorRequestMaterializer
{
    public static ConnectorPreparedRequest Prepare(PluginProviderOperationDeclaration operation,
        JsonElement input, string channelId)
    {
        var http = operation.Http ?? throw new InvalidOperationException("A closed request mapping is required.");
        if (input.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(channelId) && !http.Bootstrap)
            throw new InvalidOperationException("Validated input and a confirmed resource are required.");
        var query = new SortedDictionary<string, string>(http.QueryConstants.ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal);
        foreach (var field in http.QueryInputs)
        {
            var value = At(input, field.Value);
            if (value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                query.Add(field.Key, Scalar(value.Value));
        }
        if (http.BoundResourceQuery is not null) query.Add(http.BoundResourceQuery, channelId);
        JsonObject body = http.BodyConstants is { } constants ? JsonNode.Parse(constants.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("Body constants must be an object.") : new();
        foreach (var field in http.BodyInputs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var value = At(input, field.Value);
            if (value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                Set(body, field.Key, JsonNode.Parse(value.Value.GetRawText()));
        }
        var resources = http.ResourceChecks.Select(check => new PreparedResourceCheck(check,
            RequiredScalar(input, check.InputPointer))).ToArray();
        var media = http.MediaInput is not null ? RequiredScalar(input, http.MediaInput) : null;
        if (media is not null && !Guid.TryParse(media, out _)) throw new InvalidOperationException("Media must be an organization asset ID.");
        return new(http.Method, Query(http.Endpoint, query), body.Count == 0 ? null : Canonical(JsonSerializer.SerializeToElement(body)),
            http.Connection, channelId, operation.Effect, resources, media, http.SecretResponseFields);
    }

    public static string Query(string endpoint, IEnumerable<KeyValuePair<string, string>> query) => endpoint +
        (query.Any() ? "?" + string.Join("&", query.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))) : "");

    public static JsonElement? At(JsonElement value, string pointer)
    {
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child)) value = child;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(key, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else return null;
        }
        return value;
    }

    public static string Hash(JsonElement input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(input)))).ToLowerInvariant();
    public static string Canonical(JsonElement input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, input);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!seen.Add(property.Name)) throw new InvalidOperationException("Duplicate JSON properties are not permitted.");
                writer.WritePropertyName(property.Name); Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static void Set(JsonObject root, string pointer, JsonNode? value)
    {
        var parts = pointer.Split('/').Skip(1).ToArray();
        JsonObject current = root;
        foreach (var part in parts.SkipLast(1))
        {
            if (current[part] is null) current[part] = new JsonObject();
            current = current[part] as JsonObject ?? throw new InvalidOperationException("Conflicting body mapping.");
        }
        if (current.ContainsKey(parts[^1])) throw new InvalidOperationException("Input cannot replace a fixed request value.");
        current[parts[^1]] = value;
    }
    private static string RequiredScalar(JsonElement input, string pointer) => At(input, pointer) is { } value
        ? Scalar(value) : throw new InvalidOperationException($"Missing required resource at {pointer}.");
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!, JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true", JsonValueKind.False => "false",
        _ => throw new InvalidOperationException("Query/resource inputs must be scalar values.")
    };
}
