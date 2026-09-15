using CSweet.Contracts.Setup;

namespace CSweet.UI.Services;

public static class OfficeUpdatePresentation
{
    public static bool NeedsUpdate(string installed, string latest) =>
        Version.TryParse(installed, out var current) && Version.TryParse(latest, out var target) &&
        Normalize(current) < Normalize(target);

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static string TargetVersion(ExecutionNodeSummaryResponse office, OfficeUpdateCheckResponse check) =>
        check.Packages.FirstOrDefault(x => x.OfficeId == office.Id)?.Version ?? check.LatestVersion;

    public static string Status(ExecutionNodeSummaryResponse office, OfficeUpdateCheckResponse check)
    {
        if (!Version.TryParse(office.NodeVersion, out _)) return "Installed version unknown";
        var package = check.Packages.FirstOrDefault(x => x.OfficeId == office.Id);
        if (package is null && check.Source == "local-setup") return "Published updates unavailable";
        var version = TargetVersion(office, check);
        if (!NeedsUpdate(office.NodeVersion, version)) return package?.Source == "local-setup" ? "Matches or exceeds local source version" : "No newer release";
        return check.Packages.Any(x => x.OfficeId == office.Id)
            ? package?.Source == "local-setup" ? $"Local setup {version} available" : $"Version {version} available"
            : "No compatible installer available";
    }
}
