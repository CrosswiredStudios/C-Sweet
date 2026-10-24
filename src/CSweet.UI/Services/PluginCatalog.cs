namespace CSweet.UI.Services;

/// <summary>The bundled discovery catalog. Installation always re-reads the repository manifest and requires review.</summary>
public sealed record PluginCatalogEntry(string Id, string Name, string DescriptionKey, string Category, string RepositoryName, string Icon)
{
    public string RepositoryUrl => $"https://github.com/CrosswiredStudios/{RepositoryName}";
}

public static class PluginCatalog
{
    public static IReadOnlyList<PluginCatalogEntry> Entries { get; } =
    [
        new("com.csweet.connector.youtube", "YouTube Connection", "Plugins.Catalog.YouTube", "Connections", "CSweet.Plugin.Connector.YouTube", MudBlazor.Icons.Material.Outlined.VideoLibrary),
        new("com.csweet.web-previews", "Web Previews", "Plugins.Catalog.WebPreviews", "Development", "CSweet.Plugins.WebPreviews", MudBlazor.Icons.Material.Outlined.Web),
        new("com.csweet.toolchain.godot", "Godot Toolchain", "Plugins.Catalog.Godot", "Development", "CSweet.Plugins.Toolchain.Godot", MudBlazor.Icons.Material.Outlined.SportsEsports),
        new("com.csweet.toolchain.node-typescript", "Node & TypeScript", "Plugins.Catalog.Node", "Development", "CSweet.Plugins.Toolchain.NodeTypeScript", MudBlazor.Icons.Material.Outlined.Code),
        new("com.csweet.communication.discord", "Discord", "Plugins.Catalog.Discord", "Connections", "CSweet.Plugins.Communication.Discord", MudBlazor.Icons.Material.Outlined.Forum)
    ];
}
