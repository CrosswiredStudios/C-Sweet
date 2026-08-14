using CSweet.Contracts.Agents;

namespace CSweet.UI.Services;

public static class BusinessOnboardingAgentInstallationSelector
{
    public static bool RequiresBuildRetry(AgentInstallationResponse installation) =>
        installation.Build?.Status is "Failed" or "Cancelled";

    public static AgentInstallationResponse? FindReusable(
        IReadOnlyList<AgentInstallationResponse> installations,
        Guid packageVersionId,
        string agentId) =>
        installations
            .Where(installation =>
                installation.PackageVersionId == packageVersionId &&
                string.Equals(installation.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(installation.BusinessId, "default", StringComparison.OrdinalIgnoreCase) &&
                installation.IsEnabled &&
                string.Equals(installation.PluginKind, "Agent", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(installation.RevisionStatus, "Active", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(installation => installation.CreatedAt)
            .FirstOrDefault();
}
