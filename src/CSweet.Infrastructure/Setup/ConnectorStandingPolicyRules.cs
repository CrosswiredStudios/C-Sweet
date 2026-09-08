using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using Microsoft.AspNetCore.WebUtilities;

namespace CSweet.Infrastructure.Setup;

/// <summary>Bounded native policy predicates over reviewed request fields. No expressions or provider vocabulary.</summary>
public static class ConnectorStandingPolicyRules
{
    public static IReadOnlyList<ConnectorPolicyField> Fields(PluginProviderOperationDeclaration operation)
    {
        var http = operation.Http ?? throw new ArgumentException("A reviewed HTTP operation is required.");
        return http.BodyInputs.Keys.Select(x => new ConnectorPolicyField("body", x))
            .Concat(http.QueryInputs.Keys.Select(x => new ConnectorPolicyField("query", x)))
            .OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }

    public static bool CanAuthorize(PluginProviderOperationDeclaration operation) =>
        operation.Effect == "write" && operation.Http is { Bootstrap: false, Method: "POST" or "PUT" or "PATCH" };

    public static void Validate(ConnectorStandingPolicyDefinition definition, PluginProviderOperationDeclaration operation,
        DateTimeOffset now)
    {
        if (!CanAuthorize(operation)) throw new ArgumentException("This effect always requires an explicit decision.");
        var expected = Fields(operation);
        if (definition.Fields is null || definition.Fields.Count > 64 || definition.Fields.Count != expected.Count ||
            definition.Fields.Any(x => x is null || x.Field is null) ||
            definition.Fields.Select(x => x.Field).Distinct().Count() != definition.Fields.Count ||
            expected.Except(definition.Fields.Select(x => x.Field)).Any())
            throw new ArgumentException("Explicitly constrain or allow every reviewed mutable request field.");
        foreach (var rule in definition.Fields)
        {
            if (rule.AllowedValues is null || (rule.AllowAny ? rule.AllowedValues.Count != 0 : rule.AllowedValues.Count is < 1 or > 32) ||
                rule.AllowedValues.Any(value => value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) ||
                    Encoding.UTF8.GetByteCount(value.GetRawText()) > 2048 || rule.Field.Source == "query" && value.ValueKind != JsonValueKind.String))
                throw new ArgumentException("Field rules require bounded literal values or an explicit allow-any choice.");
        }
        if (definition.AllowedUtcDays is null || definition.AllowedUtcDays.Count is < 1 or > 7 ||
            definition.AllowedUtcDays.Distinct().Count() != definition.AllowedUtcDays.Count || definition.AllowedUtcDays.Any(x => x is < 0 or > 6) ||
            definition.StartUtcMinute is < 0 or >= 1440 || definition.EndUtcMinute is < 1 or > 1440 || definition.StartUtcMinute >= definition.EndUtcMinute ||
            definition.MaximumActionsPerHour is < 1 or > 1000 || definition.NotBefore == default ||
            definition.ExpiresAt <= now || definition.ExpiresAt <= definition.NotBefore || definition.ExpiresAt > now.AddDays(366))
            throw new ArgumentException("Choose a bounded policy lifetime, daily schedule and hourly action limit.");
        if (definition.EscalationTerms is null || definition.EscalationTerms.Count > 50 ||
            definition.EscalationTerms.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 80 || x.Any(char.IsControl)))
            throw new ArgumentException("Escalation terms must be bounded plain text.");
        if (definition.ScheduledAt is not null && !expected.Contains(definition.ScheduledAt))
            throw new ArgumentException("A scheduled time must be one of the reviewed mutable fields.");
        if (operation.Http!.MediaInput is not null)
        {
            if (definition.MaximumMediaBytes is null or <= 0 or > 274877906944L ||
                definition.AllowedMediaTypes is null || definition.AllowedMediaTypes.Count is < 1 or > 16 ||
                definition.AllowedMediaTypes.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128 || x.Any(char.IsControl) || !x.Contains('/')))
                throw new ArgumentException("A media policy requires explicit file-size and content-type limits.");
        }
        else if (definition.MaximumMediaBytes is not null || definition.AllowedMediaTypes is not null)
            throw new ArgumentException("Only media operations may declare media limits.");
    }

    public static bool Matches(ConnectorStandingPolicyDefinition definition, FrozenConnectorPlan plan, DateTimeOffset now)
    {
        if (plan.Request.Effect != "write" || plan.Request.Method is not ("POST" or "PUT" or "PATCH") ||
            now < definition.NotBefore || now >= definition.ExpiresAt || !InSchedule(definition, now)) return false;
        using var body = JsonDocument.Parse(plan.Request.Body ?? "{}", new JsonDocumentOptions { MaxDepth = 32 });
        _ = ConnectorRequestMaterializer.Hash(body.RootElement); // Duplicate keys fail closed, never ambiguous matching.
        var query = QueryHelpers.ParseQuery(new Uri(plan.Request.Url).Query);
        if (query.Any(x => x.Value.Count != 1)) return false;
        JsonElement? Value(ConnectorPolicyField field) => field.Source switch
        {
            "body" => ConnectorRequestMaterializer.At(body.RootElement, field.Path),
            "query" => query.TryGetValue(field.Path, out var value) ? JsonSerializer.SerializeToElement(value.ToString()) : null,
            _ => throw new ArgumentException("Unknown policy field source.")
        };
        foreach (var rule in definition.Fields)
        {
            var value = Value(rule.Field);
            if (value is null) { if (!rule.AllowOmission) return false; continue; }
            if (!rule.AllowAny && !rule.AllowedValues.Any(x => JsonElement.DeepEquals(x, value.Value))) return false;
        }
        if (definition.ScheduledAt is { } scheduledField && Value(scheduledField) is { } scheduledValue)
        {
            var text = scheduledValue.ValueKind == JsonValueKind.String ? scheduledValue.GetString() : null;
            // Require an explicit offset. The host must never interpret provider time in machine-local time.
            if (text is null || !(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-') ||
                !scheduledValue.TryGetDateTimeOffset(out var scheduled) ||
                scheduled < now || scheduled < definition.NotBefore || scheduled >= definition.ExpiresAt || !InSchedule(definition, scheduled)) return false;
        }
        if (plan.Media is { } media && (definition.MaximumMediaBytes is null || media.SizeBytes > definition.MaximumMediaBytes ||
            definition.AllowedMediaTypes?.Contains(media.ContentType, StringComparer.Ordinal) != true)) return false;
        return !Strings(body.RootElement).Concat(query.Select(x => x.Value.ToString()))
            .Any(text => definition.EscalationTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool InSchedule(ConnectorStandingPolicyDefinition definition, DateTimeOffset time) =>
        definition.AllowedUtcDays.Contains((int)time.UtcDateTime.DayOfWeek) &&
        time.UtcDateTime.TimeOfDay.TotalMinutes >= definition.StartUtcMinute && time.UtcDateTime.TimeOfDay.TotalMinutes < definition.EndUtcMinute;

    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(x => Strings(x.Value)),
        _ => []
    };
}
