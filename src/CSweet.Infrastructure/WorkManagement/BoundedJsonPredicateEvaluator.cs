using System.Text.Json;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Executes a deliberately small, non-code predicate language over profile metadata.</summary>
public static class BoundedJsonPredicateEvaluator
{
    public static void Validate(string jsonPath, string @operator, JsonElement expected)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || jsonPath.Length > 256 || !jsonPath.StartsWith("$.", StringComparison.Ordinal))
            throw new ArgumentException("Predicate JSON paths must be bounded absolute property paths.");
        var segments = jsonPath[2..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 8 || segments.Any(segment => segment.Length > 128 || segment.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_'))))
            throw new ArgumentException("Predicate JSON paths may contain at most eight simple property segments.");
        if (@operator is not ("equals" or "not-equals" or "array-not-empty" or "contains" or "contains-any" or "exists"))
            throw new ArgumentException($"Predicate operator '{@operator}' is not supported.");
        if (@operator == "contains-any" && expected.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The contains-any predicate requires an array value.");
        if (@operator == "contains" && expected.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            throw new ArgumentException("The contains predicate requires a scalar value.");
    }

    public static bool Evaluate(JsonElement data, string jsonPath, string @operator, JsonElement expected)
    {
        Validate(jsonPath, @operator, expected);
        var segments = jsonPath[2..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var actual = data;
        foreach (var segment in segments)
            if (actual.ValueKind != JsonValueKind.Object || !actual.TryGetProperty(segment, out actual)) return false;
        return @operator switch
        {
            "equals" => string.Equals(actual.GetRawText(), expected.GetRawText(), StringComparison.Ordinal),
            "not-equals" => !string.Equals(actual.GetRawText(), expected.GetRawText(), StringComparison.Ordinal),
            "array-not-empty" => actual.ValueKind == JsonValueKind.Array && actual.GetArrayLength() > 0,
            "contains" => actual.ValueKind == JsonValueKind.Array && actual.EnumerateArray().Any(item => SameScalar(item, expected)),
            "contains-any" => actual.ValueKind == JsonValueKind.Array && expected.ValueKind == JsonValueKind.Array &&
                              actual.EnumerateArray().Any(item => expected.EnumerateArray().Any(candidate => SameScalar(item, candidate))),
            "exists" => actual.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => false
        };
    }

    private static bool SameScalar(JsonElement left, JsonElement right) =>
        left.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object) &&
        right.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object) &&
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.OrdinalIgnoreCase);
}
