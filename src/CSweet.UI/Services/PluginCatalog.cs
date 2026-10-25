using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.UI.Services;

/// <summary>The bundled discovery catalog. Installation always re-reads the repository manifest and requires review.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginCatalogEntry(string Id, string Name, string DescriptionKey, string Category, string RepositoryName, string IconKey)
{
    public string RepositoryUrl => $"https://github.com/CrosswiredStudios/{RepositoryName}";
    public string? LogoPath { get; init; }
    public string Icon => IconKey switch
    {
        "video" => MudBlazor.Icons.Material.Outlined.VideoLibrary,
        "web" => MudBlazor.Icons.Material.Outlined.Web,
        "game" => MudBlazor.Icons.Material.Outlined.SportsEsports,
        "code" => MudBlazor.Icons.Material.Outlined.Code,
        "chat" => MudBlazor.Icons.Material.Outlined.Forum,
        _ => MudBlazor.Icons.Material.Outlined.Extension
    };
}

public static class PluginCatalog
{
    public static IReadOnlyList<PluginCatalogEntry> Entries { get; } = Load();

    private static IReadOnlyList<PluginCatalogEntry> Load()
    {
        using var source = typeof(PluginCatalog).Assembly.GetManifestResourceStream("CSweet.PluginCatalog")
            ?? throw new InvalidOperationException("The bundled plugin catalog is missing.");
        var entries = JsonSerializer.Deserialize<PluginCatalogEntry[]>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The bundled plugin catalog is empty.");
        if (entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length || entries.Any(e =>
            string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.DescriptionKey) ||
            string.IsNullOrWhiteSpace(e.Category) || string.IsNullOrWhiteSpace(e.RepositoryName) ||
            e.RepositoryName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            e.LogoPath is { } logo && (!logo.StartsWith("_content/CSweet.UI/images/brands/", StringComparison.Ordinal) ||
                logo.Contains("..", StringComparison.Ordinal) || logo.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '.' or '-' or '_')))))
            throw new InvalidOperationException("The bundled plugin catalog contains invalid metadata.");
        return Array.AsReadOnly(entries);
    }
}
