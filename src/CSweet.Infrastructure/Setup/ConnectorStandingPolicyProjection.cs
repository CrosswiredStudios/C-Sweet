using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.Plugins;
using Microsoft.AspNetCore.WebUtilities;

namespace CSweet.Infrastructure.Setup;

/// <summary>Provider-neutral native labels and choices from reviewed input schemas and the frozen request.</summary>
public static class ConnectorStandingPolicyProjection
{
    public static IReadOnlyList<ConnectorPolicyFieldReview> Fields(PluginProviderOperationDeclaration operation, FrozenConnectorPlan plan)
    {
        var http = operation.Http!;
        using var body = JsonDocument.Parse(plan.Request.Body ?? "{}");
        var query = QueryHelpers.ParseQuery(new Uri(plan.Request.Url).Query);
        return ConnectorStandingPolicyRules.Fields(operation).Select(field =>
        {
            var input = field.Source == "body" ? http.BodyInputs[field.Path] : http.QueryInputs[field.Path];
            var schema = operation.InputSchema;
            var required = true;
            foreach (var part in input.Split('/').Skip(1))
            {
                var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                required &= schema.TryGetProperty("required", out var requirements) && requirements.ValueKind == JsonValueKind.Array &&
                    requirements.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == key);
                if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(key, out schema))
                { schema = default; required = false; break; }
            }
            var label = Text(schema, "title") ?? Regex.Replace(input.Split('/').Last(), "([a-z0-9])([A-Z])", "$1 $2");
            var values = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array
                ? choices.EnumerateArray().Where(x => x.ValueKind != JsonValueKind.Null).Select(x => field.Source == "query"
                    ? JsonSerializer.SerializeToElement(x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()) : x.Clone()).ToArray() : [];
            JsonElement? current = field.Source == "body" ? ConnectorRequestMaterializer.At(body.RootElement, field.Path)?.Clone()
                : query.TryGetValue(field.Path, out var value) ? JsonSerializer.SerializeToElement(value.ToString()) : null;
            return new ConnectorPolicyFieldReview(field, label, Text(schema, "description"), current, !required || current is null, values);
        }).ToArray();
    }

    private static string? Text(JsonElement schema, string name) => schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
