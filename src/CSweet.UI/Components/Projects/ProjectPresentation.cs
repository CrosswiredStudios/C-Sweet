using System.Text.Json;
using CSweet.Contracts.Core;

namespace CSweet.UI.Components.Projects;

public static class ProjectPresentation
{
    public static string Metadata(ProjectInspectionResource resource, string key, string fallback = "Not set")
    {
        if (resource.Metadata.ValueKind != JsonValueKind.Object) return fallback;
        foreach (var property in resource.Metadata.EnumerateObject())
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : property.Value.ToString();
        return fallback;
    }

    public static string Tab(string? tab) => tab?.ToLowerInvariant() switch
    {
        "efficiency" => "efficiency", "teams" or "people" => "people", "work" => "work",
        "documents" => "documents", "communications" => "communications",
        "governance" or "decisions" => "decisions", "evidence" or "delivery" => "delivery", "audit" => "audit",
        _ => "overview"
    };

    public static string Profile(string? key) => key switch
    {
        "software-prototype.v1" => "Software prototype",
        null or "" => "No profile", _ => key
    };

    public static bool Matches(ProjectPortfolioItem item, string search) => string.IsNullOrWhiteSpace(search) ||
        $"{item.Name} {item.Outcome} {item.ProfileKey} {item.AccountableManagerName}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
