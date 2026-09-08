using CSweet.Contracts.Agents;

namespace CSweet.UI.Services;

public static class InstalledAgentPresentation
{
    public static bool IsBuilding(AgentInstallationResponse installation, bool requestPending = false) =>
        requestPending || installation.Build?.Status is "Queued" or "Cloning" or "Building" ||
        (installation.Build is null && installation.SetupState == "Building");
}
