using System.Text.Json;
using CSweet.Contracts.Agents;

namespace CSweet.Infrastructure.Agents;

public static class HarnessJudgments
{
    public static string[] Options(string kind) => kind switch
    {
        "Relevance" => ["Relevant", "NotRelevant", "Uncertain"],
        "Risk" => ["Low", "Medium", "High", "Uncertain"],
        "Completeness" => ["Complete", "Incomplete", "Uncertain"],
        _ => throw new ArgumentException("Select Relevance, Risk or Completeness. Judgment options are constructed by the platform.")
    };
    public static HarnessJudgmentResult Validate(IReadOnlyList<string> options, string text)
    {
        try
        {
            if (text.Length > 4096) return new(false, null, "Judgment output is too large.");
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("choice", out var choice) || choice.ValueKind != JsonValueKind.String ||
                !options.Contains(choice.GetString(), StringComparer.Ordinal)) return new(false, null, "The judgment did not match an allowed choice.");
            return new(true, choice.GetString(), null);
        }
        catch (JsonException) { return new(false, null, "The judgment did not return valid JSON."); }
    }
}
