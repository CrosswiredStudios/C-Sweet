using System.Text.RegularExpressions;
using CSweet.Application.Setup;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Matches release assets produced by the agent <c>pack-csab.py</c> workflow
/// (<c>&lt;Agent&gt;-&lt;version&gt;-linux-x64.csab</c> plus an optional
/// <c>.sha256</c> sidecar) and parses versions from <c>vX.Y.Z</c> tags.
/// </summary>
public static partial class PrebuiltBundleAssets
{
    public const string BundleExtension = ".csab";
    public const string ChecksumExtension = ".csab.sha256";

    public static bool IsBundleAsset(string assetName) =>
        assetName.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsChecksumSidecar(string assetName) =>
        assetName.EndsWith(ChecksumExtension, StringComparison.OrdinalIgnoreCase);

    public static GitHubReleaseAssetInfo? SelectBundle(
        GitHubReleaseInfo release,
        string operatingSystem = "linux",
        string architecture = "x64")
    {
        var suffix = $"-{operatingSystem}-{architecture}{BundleExtension}";
        return release.Assets
            .Where(x => IsBundleAsset(x.Name))
            .OrderByDescending(x => x.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static GitHubReleaseAssetInfo? FindChecksumSidecar(
        GitHubReleaseInfo release,
        GitHubReleaseAssetInfo bundle) =>
        release.Assets.FirstOrDefault(x =>
            string.Equals(x.Name, bundle.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a <c>vX.Y.Z</c> tag (or bare <c>X.Y.Z</c>) into its version text.</summary>
    public static string? ParseVersionFromTag(string tagName)
    {
        var candidate = tagName.Trim();
        if (candidate.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[1..];
        return SemanticVersionRegex().IsMatch(candidate) ? candidate : null;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemanticVersionRegex();
}
