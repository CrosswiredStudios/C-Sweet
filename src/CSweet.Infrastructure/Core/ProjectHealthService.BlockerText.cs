using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private async Task<string> StageBlockerTextAsync(Guid stageId, string fallback, CancellationToken token)
    {
        var result = await db.WorkExecutionAttempts.AsNoTracking().Where(x => x.StageExecutionId == stageId)
            .OrderByDescending(x => x.Attempt).Select(x => x.ResultJson).FirstOrDefaultAsync(token);
        var text = DecisionBlockerText(result) ?? ProjectHealthReader.Safe(fallback);
        return text.Length <= 2500 ? text : text[..2497] + "...";
    }

    internal static string? DecisionBlockerText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
            var diagnostics = Property(root, "diagnostics");
            if (diagnostics.ValueKind != JsonValueKind.Array || !diagnostics.EnumerateArray().Any(x => x.GetString() == "decision-required:v1")) return null;
            var criteria = Property(Property(root, "output"), "criteria");
            if (criteria.ValueKind != JsonValueKind.Array) return null;
            var gaps = new List<string>();
            foreach (var criterion in criteria.EnumerateArray())
            {
                if (Property(criterion, "unverifiable").ValueKind != JsonValueKind.True) continue;
                var missing = Property(criterion, "missingEvidence");
                var evidence = Property(criterion, "evidence");
                var text = missing.ValueKind == JsonValueKind.String ? missing.GetString() : null;
                if (string.IsNullOrWhiteSpace(text) && evidence.ValueKind == JsonValueKind.String)
                    text = string.Join(" ", Regex.Split(evidence.GetString()!, @"(?<=[.!?;])\s+|[\r\n]+").Where(x =>
                        Regex.IsMatch(x, @"\b(unavailable|unreachable|missing|cannot|can't|not available|not installed|not supported|Chromium only)\b", RegexOptions.IgnoreCase)));
                if (string.IsNullOrWhiteSpace(text)) text = "A required check could not be performed; see the full QA report.";
                text = ProjectHealthReader.Safe(text);
                gaps.Add(text.Length <= 700 ? text : text[..697] + "...");
            }
            return gaps.Count == 0 ? null : "QA needs a decision. Missing verification: " + string.Join(" ", gaps.Take(3));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }
}
