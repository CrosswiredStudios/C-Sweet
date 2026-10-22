using System.Globalization;
using CSweet.Contracts.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.WorkBoards;

public static class WorkBoardWorkspacePresentation
{
    public static string? DependencyWait(WorkBoardItemResponse item, IReadOnlyList<WorkBoardItemResponse> items)
        => DependencyWait(item, items.ToDictionary(x => x.Id, x => new WorkDependencyState(x.Id, x.Identifier ?? x.Title, x.Status, x.DependencyItemIds)));

    public static string? DependencyWait(WorkBoardItemResponse item, IReadOnlyDictionary<Guid, WorkDependencyState> states)
    {
        if (item.Status is not ("Ready" or "Backlog")) return null;
        var roots = WorkDependencyStatus.UnmetRoots(item.Id, states);
        return roots.Count == 0 ? null : "Waiting for " + string.Join(", ", roots.Take(3).Select(x => $"{x.Label} ({x.Status})")) +
            (roots.Count > 3 ? $" and {roots.Count - 3} more prerequisites" : "");
    }
    /// <summary>Caps card display text without changing stored content or splitting a text element.</summary>
    public static string CardText(string? value, int maximumLength = 80)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var boundaries = StringInfo.ParseCombiningCharacters(value);
        if (boundaries.Length <= maximumLength) return value;
        return maximumLength == 1 ? "…" : value[..boundaries[maximumLength - 1]].TrimEnd() + "…";
    }

    public static Guid? SelectSprint(IEnumerable<WorkSprintResponse> sprints, Guid? selectedId)
    {
        var available = sprints.ToList();
        return available.FirstOrDefault(x => x.Id == selectedId)?.Id ??
               available.FirstOrDefault(x => x.Status == "Active")?.Id ??
               available.FirstOrDefault(x => x.Status == "Paused")?.Id;
    }

    public static IReadOnlyList<WorkBoardItemResponse> ScopeItems(
        IEnumerable<WorkBoardItemResponse> items, WorkBoardView view, Guid? sprintId) =>
        items.Where(item => view switch
        {
            WorkBoardView.Sprint => sprintId.HasValue && item.SprintId == sprintId && item.ExecutionMode == Wire.WorkItemExecutionModes.Executable,
            WorkBoardView.Backlog => item.SprintId is null && item.Status is not ("Completed" or "Cancelled"),
            _ => true
        }).OrderBy(x => x.Rank).ToList();

    public static string ShortIdentifier(WorkBoardItemResponse item, string boardKey)
    {
        var prefix = boardKey + "-";
        return !string.IsNullOrWhiteSpace(boardKey) && item.Identifier?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? "#" + item.Identifier[prefix.Length..] : item.Identifier ?? item.Kind;
    }

    public static string TypeLabel(WorkBoardItemResponse item)
    {
        if (string.IsNullOrWhiteSpace(item.TypeKey)) return item.Kind;
        var parts = item.TypeKey.Split('.');
        var name = parts.Length >= 2 && parts[^1].StartsWith('v') ? parts[^2] : item.Kind;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('-', ' '));
    }

    public static string? AttentionReason(WorkBoardItemResponse item, Wire.WorkItemExecutionResponse? execution)
    {
        if (item.Status is "Completed" or "Cancelled") return null;
        if (!string.IsNullOrWhiteSpace(execution?.BlockedReason)) return execution.BlockedReason;
        if (execution?.Status is "Blocked" or "Failed") return $"Execution {execution.Status.ToLowerInvariant()}";
        if (item.Approvals.Any(x => x.PlanningRevision == item.PlanningRevision && x.Status == Wire.WorkItemApprovalStatuses.ChangesRequested))
            return "Approval changes requested";
        if (item.Approvals.Any(x => x.PlanningRevision == item.PlanningRevision && x.Status == Wire.WorkItemApprovalStatuses.Pending))
            return "Awaiting specialist approval";
        return item.Status == "Blocked" ? "Work is blocked" : null;
    }

    /// <summary>A blocked or failed ticket can be returned to work by anyone allowed to move it or retry stages.</summary>
    public static bool CanRetry(WorkBoardItemResponse item, IReadOnlyCollection<string> allowedActions) =>
        item.Status is "Blocked" or "Failed" &&
        (allowedActions.Contains(WorkItemActions.Move) || allowedActions.Contains(WorkOrchestrationActions.Retry));

    public static bool Matches(WorkBoardItemResponse item, string? search) =>
        WorkBoardPresentation.Matches(item, search) ||
        (!string.IsNullOrWhiteSpace(search) && item.AssignedDisplayName?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    public static double CompletionPercent(WorkSprintResponse sprint) => sprint.ItemCount <= 0
        ? 0 : Math.Clamp(100d * sprint.CompletedItemCount / sprint.ItemCount, 0, 100);
}

public enum WorkBoardView { Sprint, Backlog, All, Sprints }

public sealed record WorkBoardDropRequest(WorkBoardItemResponse Item, WorkBoardColumnResponse Column, Guid? BeforeItemId);
