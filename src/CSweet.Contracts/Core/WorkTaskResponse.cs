namespace CSweet.Contracts.Core;

public sealed record WorkTaskResponse(
    Guid Id,
    Guid OrganizationId,
    Guid? StrategicObjectiveId,
    Guid? AssignedRoleId,
    Guid? AssignedWorkerId,
    string Title,
    string Description,
    int Status,
    int Priority,
    DateTimeOffset? DueDate,
    bool RequiresApproval,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? BoardId = null,
    Guid? BoardColumnId = null,
    long BoardRank = 0,
    long Revision = 1,
    string Kind = "Task",
    Guid? ParentWorkTaskId = null,
    Guid? SprintId = null,
    decimal? EstimatePoints = null)
{
    public IReadOnlyList<CSweet.WorkManagement.Contracts.WorkItemMentionSpan> Mentions { get; init; } = [];
}
