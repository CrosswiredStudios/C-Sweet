using CSweet.Contracts.WorkManagement;

namespace CSweet.UI.Components.WorkBoards;

public static class WorkBoardPresentation
{
    public static string DescriptionPreview(string? value, int maximumLength = 100)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (maximumLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        var normalized = string.Join(' ', value.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength]}…";
    }

    public static bool Matches(WorkBoardItemResponse item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var term = search.Trim();
        return item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Kind.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Priority.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               (item.Identifier?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static IReadOnlyList<WorkBoardItemResponse> ItemsForColumn(
        IEnumerable<WorkBoardItemResponse> items,
        Guid columnId,
        string? search) =>
        items.Where(item => item.ColumnId == columnId && Matches(item, search))
            .OrderBy(item => item.Rank)
            .ToList();

    public static MoveBoardWorkItemRequest MoveRequest(
        WorkBoardItemResponse item,
        Guid targetColumnId,
        Guid? beforeItemId) =>
        new(targetColumnId, beforeItemId == item.Id ? null : beforeItemId, item.Revision);

    public static bool CanMove(
        WorkBoardDetailResponse? detail,
        WorkBoardItemResponse item,
        WorkBoardColumnResponse target)
    {
        if (detail is null || detail.Board.IsArchived) return false;
        var action = target.Category switch
        {
            "Done" when item.Status != "Completed" => WorkItemActions.Complete,
            "Cancelled" when item.Status != "Cancelled" => WorkItemActions.Cancel,
            "ToDo" or "InProgress" when item.Status is "Completed" or "Cancelled" => WorkItemActions.Reopen,
            _ => WorkItemActions.Move
        };
        return detail.Board.AllowedActions.Contains(action);
    }
}
