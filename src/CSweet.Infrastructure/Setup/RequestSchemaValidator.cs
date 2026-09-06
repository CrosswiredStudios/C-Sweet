using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.Infrastructure.Setup;

public static class RequestSchemaValidator
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
        "minLength", "maxLength", "minimum", "exclusiveMinimum", "maximum", "format", "enum",
        "pattern", "uniqueItems", "$defs", "$ref", "description", "title"
    ];

    public static void Validate(JsonElement value, JsonElement schema) =>
        Validate(value, schema, schema, "$", 0);

    public static void ValidateSchema(JsonElement schema) =>
        ValidateSchema(schema, schema, "$", 0);

    private static void ValidateSchema(JsonElement schema, JsonElement rootSchema, string path, int depth)
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
        if (schema.TryGetProperty("uniqueItems", out var uniqueItems) &&
            uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException(
                $"JSON Schema '{path}.uniqueItems' must be boolean.");
        if (schema.TryGetProperty("pattern", out var pattern))
            ValidatePattern(pattern, path);
        if (schema.TryGetProperty("$ref", out var reference))
            _ = ResolveReference(rootSchema, reference, path);
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"JSON Schema '{path}.properties' must be an object.");
            foreach (var property in properties.EnumerateObject())
                ValidateSchema(property.Value, rootSchema, $"{path}.properties.{property.Name}", depth + 1);
        }
        if (schema.TryGetProperty("items", out var items))
            ValidateSchema(items, rootSchema, $"{path}.items", depth + 1);
        if (schema.TryGetProperty("$defs", out var definitions))
        {
            if (definitions.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"JSON Schema '{path}.$defs' must be an object.");
            foreach (var definition in definitions.EnumerateObject())
                ValidateSchema(definition.Value, rootSchema, $"{path}.$defs.{definition.Name}", depth + 1);
        }
    }

    private static void Validate(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidOperationException("JSON exceeds the maximum validation depth.");
        if (schema.TryGetProperty("$ref", out var reference))
            Validate(value, ResolveReference(rootSchema, reference, path), rootSchema, path, depth + 1);
        ValidateType(value, schema, path);
        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(value, schema, rootSchema, path, depth);
        else if (value.ValueKind == JsonValueKind.Array)
            ValidateArray(value, schema, rootSchema, path, depth);
        else if (value.ValueKind == JsonValueKind.String)
            ValidateString(value.GetString()!, schema, path);
        else if (value.ValueKind == JsonValueKind.Number)
            ValidateNumber(value, schema, path);
        ValidateEnum(value, schema, path);
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        int depth)
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
                Validate(property.Value, childSchema, rootSchema, $"{path}.{property.Name}", depth + 1);
            else if (!additionalAllowed)
                Fail($"{path}.{property.Name}", "is not allowed");
        }
        var count = value.EnumerateObject().Count();
        if (schema.TryGetProperty("minProperties", out var min) && count < min.GetInt32())
            Fail(path, "contains too few properties");
        if (schema.TryGetProperty("maxProperties", out var max) && count > max.GetInt32())
            Fail(path, "contains too many properties");
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        int depth)
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
                Validate(item, itemSchema, rootSchema, $"{path}[{index++}]", depth + 1);
        }
        if (schema.TryGetProperty("uniqueItems", out var uniqueItems) && uniqueItems.GetBoolean())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
                if (!seen.Add(item.GetRawText()))
                    Fail(path, "contains duplicate items");
        }
    }

    private static void ValidateString(string value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32())
            Fail(path, "is too short");
        if (schema.TryGetProperty("maxLength", out var max) && value.Length > max.GetInt32())
            Fail(path, "is too long");
        if (!schema.TryGetProperty("format", out var format))
        {
            ValidateStringPattern(value, schema, path);
            return;
        }
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
        ValidateStringPattern(value, schema, path);
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path)
    {
        var number = value.GetDecimal();
        if (schema.TryGetProperty("minimum", out var min) && number < min.GetDecimal())
            Fail(path, "is below the minimum");
        if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMin) && number <= exclusiveMin.GetDecimal())
            Fail(path, "is not above the exclusive minimum");
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

    private static void ValidateStringPattern(string value, JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("pattern", out var pattern))
            return;
        try
        {
            if (!Regex.IsMatch(
                    value,
                    pattern.GetString()!,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(100)))
                Fail(path, "does not match the required pattern");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"JSON Schema '{path}.pattern' is invalid.", exception);
        }
    }

    private static void ValidatePattern(JsonElement pattern, string path)
    {
        if (pattern.ValueKind != JsonValueKind.String || pattern.GetString()!.Length > 512)
            throw new InvalidOperationException($"JSON Schema '{path}.pattern' must be a bounded string.");
        try
        {
            _ = new Regex(
                pattern.GetString()!,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"JSON Schema '{path}.pattern' is invalid.", exception);
        }
    }

    private static JsonElement ResolveReference(JsonElement rootSchema, JsonElement reference, string path)
    {
        if (reference.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"JSON Schema '{path}.$ref' must be a string.");
        var value = reference.GetString()!;
        const string prefix = "#/$defs/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length ||
            value[prefix.Length..].Contains('/', StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"JSON Schema '{path}.$ref' must target a root $defs entry.");
        var name = value[prefix.Length..].Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        if (!rootSchema.TryGetProperty("$defs", out var definitions) ||
            definitions.ValueKind != JsonValueKind.Object ||
            !definitions.TryGetProperty(name, out var target) ||
            target.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"JSON Schema '{path}.$ref' targets an unknown definition.");
        return target;
    }

    private static void Fail(string path, string reason) =>
        throw new InvalidOperationException($"JSON Schema validation failed: {path} {reason}.");
}
