using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace CSweet.Infrastructure.Persistence;

public static class MutationContent
{
    public static string Digest<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, JsonSerializer.SerializeToElement(value));
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Write(writer, p.Value); }
            writer.WriteEndObject();
        } else if (value.ValueKind == JsonValueKind.Array) {
            writer.WriteStartArray(); foreach (var child in value.EnumerateArray()) Write(writer, child); writer.WriteEndArray();
        } else value.WriteTo(writer);
    }
}
