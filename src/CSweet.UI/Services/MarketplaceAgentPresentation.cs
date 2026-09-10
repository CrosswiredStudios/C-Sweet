using System.Globalization;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;

namespace CSweet.UI.Services;

public static partial class MarketplaceAgentPresentation
{
    public const string DefaultImageUrl = "_content/CSweet.UI/images/agent-default.svg";
    public const string CSweetCompanyLogoUrl = "_content/CSweet.UI/images/csweet-icon.svg";
    public static bool IsCompanyLogoUrl(string? value) =>
        AgentCatalogBranding.IsImageUrl(value) || value == CSweetCompanyLogoUrl;

    public const string DefaultAccentColor = "#1C6252";
    public static bool IsAccentColor(string? value) => AgentCatalogBranding.IsAccentColor(value);

    // Only this bundled artwork directory is accepted as a local image source.
    // External manifests keep the SDK's absolute-HTTPS requirement.
    public static bool IsAgentImageUrl(string? value) =>
        AgentCatalogBranding.IsImageUrl(value) ||
        (value is not null && BundledPortraitPath().IsMatch(value));

    [GeneratedRegex(@"\A_content/CSweet\.UI/images/agents/[a-z0-9]+(?:-[a-z0-9]+)*-v[1-9][0-9]*\.jpg\z", RegexOptions.CultureInvariant)]
    private static partial Regex BundledPortraitPath();

    public static string AccentStyle(string? value)
    {
        var color = AgentCatalogBranding.IsAccentColor(value) ? value! : DefaultAccentColor;
        // Choose the higher-contrast foreground even for very light publisher accents.
        var red = Channel(color.AsSpan(1, 2));
        var green = Channel(color.AsSpan(3, 2));
        var blue = Channel(color.AsSpan(5, 2));
        var luminance = .2126 * red + .7152 * green + .0722 * blue;
        var foreground = luminance > .179 ? "#000000" : "#FFFFFF";
        return $"--agent-accent: {color}; --agent-accent-foreground: {foreground};";
    }

    private static double Channel(ReadOnlySpan<char> hex)
    {
        var channel = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
    }
}
