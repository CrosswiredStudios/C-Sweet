using System.Globalization;
using CSweet.Contracts.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.WorkBoards;

public static class WorkBoardWorkspacePresentation
{
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
            WorkBoardView.Sprint => sprintId.HasValue && item.SprintId == sprintId,
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

    public static bool Matches(WorkBoardItemResponse item, string? search) =>
        WorkBoardPresentation.Matches(item, search) ||
        (!string.IsNullOrWhiteSpace(search) && item.AssignedDisplayName?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    public static double CompletionPercent(WorkSprintResponse sprint) => sprint.ItemCount <= 0
        ? 0 : Math.Clamp(100d * sprint.CompletedItemCount / sprint.ItemCount, 0, 100);
}

public enum WorkBoardView { Sprint, Backlog, All, Sprints }

public sealed record WorkBoardDropRequest(WorkBoardItemResponse Item, WorkBoardColumnResponse Column, Guid? BeforeItemId);
