using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

public static class BusinessOperatingProfileRecommender
{
    public const string ConfigurationKey = "businessOperatingProfile";

    private static readonly (string Key, string[] Signals)[] Rules =
    [
        ("game-studio", ["game studio", "video game", "videogame", "gaming", "indie game", "game developer"]),
        ("saas", ["saas", "software as a service", "subscription software", "cloud software", "web application", "software platform"]),
        ("ecommerce", ["ecommerce", "e-commerce", "online store", "storefront", "online retail"]),
        ("professional-services", ["consulting", "consultancy", "agency", "advisory", "professional services", "services firm"]),
        ("media-content", ["youtube", "media company", "content creator", "content studio", "podcast", "newsletter", "video channel"])
    ];

    public static string Recommend(
        string? industry,
        string? mission,
        IReadOnlyList<PluginConfigurationOption>? availableOptions)
    {
        var available = (availableOptions ?? [])
            .Select(x => x.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = $" {industry} {mission} ";
        foreach (var (key, signals) in Rules)
        {
            if (available.Contains(key) && signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                return key;
        }
        return available.Contains("general") ? "general" : available.FirstOrDefault() ?? "general";
    }
}
