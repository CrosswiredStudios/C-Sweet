using CSweet.Contracts.Core;

namespace CSweet.UI.Components;

internal static class CurrentActivityNavigation
{
    public static string WorkItemHref(Guid organizationId, CurrentActivityItem item) =>
        item.PersonalBoardOwnerId is { } owner
            ? $"/organizations/{organizationId}/employees/{owner}?tab=personal-board&item={item.WorkItemId}"
            : item.ProjectId is { } project
                ? $"/organizations/{organizationId}/projects/{project}?tab=work&board={item.BoardId}&item={item.WorkItemId}"
                : $"/organizations/{organizationId}/projects/boards/{item.BoardId}?item={item.WorkItemId}";
}
