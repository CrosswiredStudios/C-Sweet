using CSweet.Contracts.Analytics;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Security;
using CSweet.Contracts.SourceControl;

namespace CSweet.Promo.Demo;

public sealed partial class FixtureHandler
{
    // Read-only, in-memory data for the actual application pages in the local design review.
    // An unknown endpoint still fails explicitly; this is not a catch-all success response.
    private object? NavigationPayload(string path)
    {
        var root = $"/api/organizations/{data.OrganizationId:D}";
        var core = $"/api/core/organizations/{data.OrganizationId:D}";
        if (path == root) return data.Organization;
        if (path == $"{core}/capability-approvals") return Array.Empty<CSweet.Contracts.Agents.CapabilityApprovalResponse>();
        if (path == $"{core}/delegations") return Array.Empty<CSweet.Contracts.Agents.DelegationResponse>();
        if (path == $"{core}/dashboard") return new CompanyDashboardResponse(new([], null), new([], null));
        if (path == $"{core}/dashboard/layout") return new DashboardLayoutRequest(DashboardWidgets.DefaultOrder);
        if (path == $"{core}/dashboard/activity") return new CurrentActivityPage(DemoData.Epoch, 0, [], null);
        if (path == $"{core}/workstreams/inspection") return data.Portfolio;
        if (path == $"{core}/workstreams/{data.ProjectId:D}/inspection") return data.Project;
        if (path == $"{root}/roles") return data.Roles;
        if (path == $"{root}/workers") return Array.Empty<WorkerResponse>();
        if (path == $"{core}/teams") return data.Teams;
        if (path == $"{core}/hiring") return new HiringDashboardResponse([], []) { CurrentOrganizationUserId = data.OwnerId };
        if (path == $"{root}/source-control/internal/repositories") return Array.Empty<SourceControlRepositorySummary>();
        var runtimeInstallation = data.Installations.FirstOrDefault(x => path == $"/api/agents/installations/{x.Id:D}/runtime/status");
        if (runtimeInstallation is not null) return new AgentRuntimeReadinessResponse(runtimeInstallation.Id, null,
            AgentRuntimeReadinessStages.Offline, "Offline", "Sample agent; runtime execution is disabled in this preview.",
            null, null, null, false, true);
        if (path == $"{root}/source-control/dashboard") return new SourceControlDashboardResponse([], [], null,
            new(false, false, "Source-control connections are not configured in this sample workspace."), false);
        if (path == $"{root}/documents") return new[] { data.Document.Document };
        if (path == $"{root}/documents/packages") return Array.Empty<object>();
        if (path == $"{root}/security/events") return new SecurityEventPageResponse([], null);
        if (path == $"{root}/analytics/inference") return new InferenceAnalyticsResponse("Last7Days", DemoData.Epoch.AddDays(-7), DemoData.Epoch,
            DemoData.Epoch, new(0, 0, 0, 0), []);
        if (path is "/api/agents/definitions" or "/api/llm-provider-profiles" or "/api/gen-ai-provider-profiles" or "/api/personal-assistant/messages" or "/api/repository-onboarding") return Array.Empty<object>();
        if (path == "/api/personal-assistant/settings") return new PersonalAssistantSettings(null, null, null, 1, false,
            "Configure a provider in the full application to use the assistant.", [], new Dictionary<string, System.Text.Json.JsonElement>());
        if (path == "/api/health") return new { status = "Sample workspace" };
        if (path == "/api/agent-runtime/settings") return new {
            id = data.Id(250), enableImportedAgents = false, defaultActivationMode = "OnDemand",
            defaultTickFrequencySeconds = 60, minimumTickFrequencySeconds = 30, defaultMaxRuntimeSeconds = 300,
            defaultOverlapPolicy = "Skip", allowAlwaysOnCommunityAgents = false, defaultRestartPolicy = "Never",
            globalMaxActiveWorkloads = 4, perBusinessMaxActiveWorkloads = 4, perInstallationMaxActiveWorkloads = 1,
            defaultWorkloadMemoryMb = 512, maximumWorkloadMemoryMb = 4096, defaultWorkloadCpuPercent = 25,
            maximumWorkloadCpuPercent = 100, defaultWorkloadProcessLimit = 64, defaultWorkloadLogLimitMb = 10,
            workloadStartTimeoutSeconds = 60, mcpSessionTimeoutSeconds = 60, workloadStopGraceSeconds = 10,
            defaultNetworkPolicy = "Isolated", allowPublicInternetByDefault = false, allowedPackageFeedHosts = "",
            blockedNetworkCidrs = "", buildTimeoutSeconds = 600, buildMemoryMb = 2048, buildCpuPercent = 50,
            maximumRepositorySizeMb = 1024, maximumBuildLogMb = 10, keepFailedBuildWorkspaces = false,
            completedRuntimeRetentionDays = 7, failedRuntimeRetentionDays = 7, buildLogRetentionDays = 7,
            removeWorkloadsAfterCompletion = true, removeWorkspacesAfterCompletion = true, updatedAt = DemoData.Epoch
        };
        return null;
    }
}
