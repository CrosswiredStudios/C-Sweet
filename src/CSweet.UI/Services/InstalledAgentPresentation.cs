using CSweet.Contracts.Agents;

namespace CSweet.UI.Services;

public static class InstalledAgentPresentation
{
    public static (string Title, string Detail) BuildProgress(AgentBuildSummaryResponse build) =>
        (build.SourceMode, build.Status) switch
        {
            ("PrebuiltRelease", "Queued") => ("Install queued", "Waiting to fetch the release bundle."),
            ("PrebuiltRelease", "Cloning") => ("Downloading release", "Downloading the tagged CSAB package."),
            ("PrebuiltRelease", "Building") => ("Installing release", "Verifying and installing the prebuilt package."),
            (_, "Queued") => ("Build queued", "Waiting for an available build worker."),
            (_, "Cloning") => ("Fetching source", "Cloning the approved GitHub commit."),
            (_, "Building") => ("Building package", "Compiling and packaging the new agent version."),
            _ => ("Updating agent", $"Package status: {build.Status}.")
        };

    public static bool IsBuilding(AgentInstallationResponse installation, bool requestPending = false) =>
        requestPending || installation.Build?.Status is "Queued" or "Cloning" or "Building" ||
        (installation.Build is null && installation.SetupState == "Building");
}
