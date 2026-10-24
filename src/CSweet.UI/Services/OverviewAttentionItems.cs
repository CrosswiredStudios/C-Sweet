using CSweet.Contracts.Communications;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;

namespace CSweet.UI.Services;

/// <summary>Shared eligibility rules for the Overview queue and its navigation badge.</summary>
public static class OverviewAttentionItems
{
    public static bool IsPending(string status) =>
        string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase);

    public static bool IsActionable(ApprovalDashboardItemResponse item) => item.CanDecide && IsPending(item.Status);

    public static bool IsActionable(HiringRecommendationResponse item) => IsPending(item.Status) && item.RemainingHeadcount > 0;

    public static bool IsActionable(ProjectIncidentAttentionResponse item) => !item.IsStatusReport;

    public static IReadOnlyList<PendingAgentQuestionResponse> StandaloneQuestions(IReadOnlyList<PendingAgentQuestionResponse>? questions,
        IReadOnlyList<ProjectIncidentAttentionResponse>? incidents) => (questions ?? []).Where(x =>
            x.Decision.IncidentId is not { } id || !(incidents ?? []).Any(i => i.Id == id)).ToArray();

    public static IReadOnlyList<ComputeResourceStatus> ComputeRequests(ComputeDashboard? dashboard) =>
        dashboard is { CanManageNetwork: true } compute
            ? compute.Resources.Where(x => x.LocalLinkRequested && !x.LocalLinkAllowed && x.LeaseExpiresAt > compute.CheckedAt &&
                x.State is not ("Destroyed" or "Destroying") && x.DesiredState != "Destroyed")
                .OrderBy(x => x.UpdatedAt).ToList()
            : [];

    public static int Count(ApprovalDashboardResponse? approvals, HiringBacklogResponse? hiring,
        IReadOnlyList<PendingAgentQuestionResponse>? questions, ComputeDashboard? compute) =>
        (approvals?.Items.Count(IsActionable) ?? 0) + (approvals?.ProjectIncidents.Count(IsActionable) ?? 0) +
        (hiring?.Recommendations.Count(IsActionable) ?? 0) +
        StandaloneQuestions(questions, approvals?.ProjectIncidents).Count(x => IsPending(x.Decision.Status)) + ComputeRequests(compute).Count;
}
