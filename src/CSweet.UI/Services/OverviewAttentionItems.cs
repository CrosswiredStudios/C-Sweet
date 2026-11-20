using CSweet.Contracts.Communications;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

/// <summary>Shared eligibility rules for the Overview queue and its navigation badge.</summary>
public static class OverviewAttentionItems
{
    // Every consumer of the shared attention projection must invalidate on the
    // same source changes, including standalone executive questions without a board.
    public static bool IsInvalidationEvent(string eventType) => eventType is
        AppRealtimeEvents.SuggestionsChanged or AppRealtimeEvents.ProjectImportChanged or AppRealtimeEvents.ApprovalChanged or
        AppRealtimeEvents.HiringRecommendationsChanged or AppRealtimeEvents.ProjectIncidentChanged or AppRealtimeEvents.ComputeChanged or
        CommunicationEvents.DecisionUpdated or AppRealtimeEvents.WorkBoardChanged or AppRealtimeEvents.ResourceChanged or AppRealtimeEvents.EmployeeDirectoryChanged;
    public static bool IsPending(string status) =>
        string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase);

    public static bool IsActionable(ApprovalDashboardItemResponse item) => item.CanDecide && IsPending(item.Status);

    public static bool IsActionable(HiringRecommendationResponse item) =>
        IsPending(item.Status) && item.RemainingHeadcount > 0 && !IsAutomaticHiringActivity(item);

    public static bool IsAutomaticHiringActivity(HiringRecommendationResponse item)
    {
        if (!IsPending(item.Status) || item.RemainingHeadcount <= 0 || !item.AutomaticHiringEnabled ||
            item.ManualSelectionRequested || item.NoSuitableAgentFound) return false;
        if (item.AutomaticHiringStatus == "WaitingDependency" && item.AutomaticHiringRetryAt.HasValue) return true;
        return string.IsNullOrWhiteSpace(item.AutomaticHiringBlockReason) &&
            item.AutomaticHiringStatus is not ("NeedsReview" or AgentHireOperationStatuses.Failed or
                AgentHireOperationStatuses.NeedsSetup or AgentHireOperationStatuses.AwaitingConfirmation);
    }

    public static bool IsActionable(ProjectIncidentAttentionResponse item) => item.HasUserAction;

    public static IReadOnlyList<PendingAgentQuestionResponse> StandaloneQuestions(IReadOnlyList<PendingAgentQuestionResponse>? questions,
        IReadOnlyList<ProjectIncidentAttentionResponse>? incidents) => (questions ?? []).Where(x =>
            x.Decision.IncidentId is not { } id || !(incidents ?? []).Any(i => i.Id == id)).ToArray();

    // Used while the full issue directory is unavailable. Only join records with an explicit link.
    public static OverviewIssueDirectory FallbackIssues(IReadOnlyList<BlockedWorkAttentionResponse>? work,
        IReadOnlyList<PendingAgentQuestionResponse>? questions, IReadOnlyList<ProjectIncidentAttentionResponse>? incidents)
    {
        var rows = new List<OverviewIssueCard>();
        var keys = new Dictionary<Guid, string>();
        foreach (var w in work ?? [])
        {
            var sources = w.Blockers.Count > 0 ? w.Blockers : [new OverviewBlockerSource(
                w.DecisionId is Guid d ? "decision:" + d.ToString("N") : "work:" + w.ItemId.ToString("N"), w.ItemId, w.Title)];
            foreach (var source in sources)
            {
                if (sources.Count == 1) keys[w.ItemId] = source.Key;
                if (w.DecisionId is Guid decision) keys[decision] = source.Key;
                rows.Add(new(source.Key, null, null, "", source.Title, "", w.OwnerName, "", w.OffersRetry, false, [w], [], [], w.Reason)
                    { OwnerOrganizationUserId = w.OwnerOrganizationUserId, AffectedWork = source.ActionUri is { } uri
                        ? [new(source.ItemId, source.Title, uri)] : [] });
            }
        }
        foreach (var i in incidents ?? [])
        {
            var key = i.AffectedWorkItemId is Guid target && keys.TryGetValue(target, out var linked) ? linked :
                (i.BlockedCoordinationId ?? i.Collaboration?.SessionId) is Guid session ? "coordination:" + session.ToString("N") : "incident:" + i.Id.ToString("N");
            keys[i.Id] = key;
            if (i.ResolutionDecision is { } decision) keys[decision.Id] = key;
            rows.Add(new(key, null, i.WorkstreamId, i.ProjectName, i.Reason, "", i.ReporterName, i.RecommendedAction,
                i.HasUserAction, false, [], [], [i], "") { OwnerOrganizationUserId = i.ReporterOrganizationUserId });
        }
        foreach (var q in (questions ?? []).Where(q => IsPending(q.Decision.Status)))
        {
            var key = keys.GetValueOrDefault(q.Decision.IncidentId ?? q.Decision.Id) ?? "question:" + q.Decision.Id.ToString("N");
            rows.Add(new(key, null, null, "", q.Decision.Prompt, "", q.AgentName, "", true, false, [], [q], [], "")
                { OwnerOrganizationUserId = q.AgentOrganizationUserId });
        }
        return OverviewIssueDirectory.Consolidate(rows);
    }

    public static IReadOnlyList<ComputeResourceStatus> ComputeRequests(ComputeDashboard? dashboard) =>
        dashboard is { CanManageNetwork: true } compute
            ? compute.Resources.Where(x => x.LocalLinkRequested && !x.LocalLinkAllowed && x.LeaseExpiresAt > compute.CheckedAt &&
                x.State is not ("Destroyed" or "Destroying") && x.DesiredState != "Destroyed")
                .OrderBy(x => x.UpdatedAt).ToList()
            : [];

    public static bool WaitingForOfficeCapacity(ComputeDashboard? dashboard) =>
        dashboard is { CanManageNetwork: true, Setup.Health: "WaitingForOffice" };

    public static int Count(ApprovalDashboardResponse? approvals, HiringBacklogResponse? hiring,
        IReadOnlyList<PendingAgentQuestionResponse>? questions, ComputeDashboard? compute,
        IReadOnlyList<BlockedWorkAttentionResponse>? blockedWork = null, OverviewIssueDirectory? issues = null) =>
        (approvals?.Items.Count(IsActionable) ?? 0) + (hiring?.Recommendations.Count(IsActionable) ?? 0) + ComputeRequests(compute).Count +
        (WaitingForOfficeCapacity(compute) ? 1 : 0) +
        (issues ?? FallbackIssues(blockedWork, questions, approvals?.ProjectIncidents)).UserActions.Count;
}
