using System.Globalization;
using System.Text.Json;

namespace CSweet.AgentHost.Broker;

internal static class JsonSchemaValidator
{
    private const int MaximumDepth = 32;
    private static readonly HashSet<string> SupportedTypes =
        ["object", "array", "string", "integer", "number", "boolean", "null"];
    private static readonly HashSet<string> SupportedFormats =
        ["uuid", "date-time", "uri", "email"];
    private static readonly HashSet<string> SupportedKeywords =
    [
        "type", "properties", "required", "additionalProperties", "items",
        "minProperties", "maxProperties", "minItems", "maxItems",
        "minLength", "maxLength", "minimum", "maximum", "format", "enum",
        "description", "title"
    ];

    public static void Validate(JsonElement value, JsonElement schema) =>
        Validate(value, schema, "$", 0);

    public static void ValidateSchema(JsonElement schema) =>
        ValidateSchema(schema, "$", 0);

    private static void ValidateSchema(JsonElement schema, string path, int depth)
    {
        if (depth > MaximumDepth || schema.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"JSON Schema '{path}' must be a bounded object schema.");
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(keyword.Name))
                throw new InvalidOperationException(
                    $"JSON Schema '{path}' uses unsupported keyword '{keyword.Name}'.");
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var values = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(x => x.GetString()).ToArray()
                : [type.GetString()];
            if (values.Any(x => x is null || !SupportedTypes.Contains(x)))
                throw new InvalidOperationException($"JSON Schema '{path}' declares an unsupported type.");
        }
        if (schema.TryGetProperty("format", out var format) &&
            (format.ValueKind != JsonValueKind.String ||
             !SupportedFormats.Contains(format.GetString()!)))
            throw new InvalidOperationException($"JSON Schema '{path}' declares an unsupported format.");
        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException(
                $"JSON Schema '{path}.additionalProperties' must be boolean.");
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"JSON Schema '{path}.properties' must be an object.");
            foreach (var property in properties.EnumerateObject())
                ValidateSchema(property.Value, $"{path}.properties.{property.Name}", depth + 1);
        }
        if (schema.TryGetProperty("items", out var items))
            ValidateSchema(items, $"{path}.items", depth + 1);
    }

    private static void Validate(JsonElement value, JsonElement schema, string path, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidOperationException("JSON exceeds the maximum validation depth.");
        ValidateType(value, schema, path);
        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(value, schema, path, depth);
        else if (value.ValueKind == JsonValueKind.Array)
            ValidateArray(value, schema, path, depth);
        else if (value.ValueKind == JsonValueKind.String)
            ValidateString(value.GetString()!, schema, path);
        else if (value.ValueKind == JsonValueKind.Number)
            ValidateNumber(value, schema, path);
        ValidateEnum(value, schema, path);
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, int depth)
    {
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray().Select(x => x.GetString()!))
                if (!value.TryGetProperty(name, out _))
                    Fail($"{path}.{name}", "is required");
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var additionalAllowed = !schema.TryGetProperty("additionalProperties", out var additional) ||
                                additional.ValueKind != JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var childSchema))
                Validate(property.Value, childSchema, $"{path}.{property.Name}", depth + 1);
            else if (!additionalAllowed)
                Fail($"{path}.{property.Name}", "is not allowed");
        }
        var count = value.EnumerateObject().Count();
        if (schema.TryGetProperty("minProperties", out var min) && count < min.GetInt32())
            Fail(path, "contains too few properties");
        if (schema.TryGetProperty("maxProperties", out var max) && count > max.GetInt32())
            Fail(path, "contains too many properties");
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path, int depth)
    {
        var count = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
            Fail(path, "contains too few items");
        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
            Fail(path, "contains too many items");
        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                Validate(item, itemSchema, $"{path}[{index++}]", depth + 1);
        }
    }

    private static void ValidateString(string value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32())
            Fail(path, "is too short");
        if (schema.TryGetProperty("maxLength", out var max) && value.Length > max.GetInt32())
            Fail(path, "is too long");
        if (!schema.TryGetProperty("format", out var format))
            return;
        var valid = format.GetString() switch
        {
            "uuid" => Guid.TryParse(value, out _),
            "date-time" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "uri" => Uri.TryCreate(value, UriKind.Absolute, out _),
            "email" => value.Contains('@', StringComparison.Ordinal) && value.Length <= 320,
            _ => true
        };
        if (!valid)
            Fail(path, $"is not a valid {format.GetString()}");
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path)
    {
        var number = value.GetDecimal();
        if (schema.TryGetProperty("minimum", out var min) && number < min.GetDecimal())
            Fail(path, "is below the minimum");
        if (schema.TryGetProperty("maximum", out var max) && number > max.GetDecimal())
            Fail(path, "is above the maximum");
    }

    private static void ValidateEnum(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            Fail(path, "is not an allowed value");
    }

    private static void ValidateType(JsonElement value, JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("type", out var type))
            return;
        var allowed = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [type.GetString()!];
        if (!allowed.Any(candidate => Matches(value, candidate)))
            Fail(path, $"must be {string.Join(" or ", allowed)}");
    }

    private static bool Matches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static void Fail(string path, string reason) =>
        throw new InvalidOperationException($"JSON Schema validation failed: {path} {reason}.");
}
