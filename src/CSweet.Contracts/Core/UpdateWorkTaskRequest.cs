namespace CSweet.Contracts.Core;

public sealed record UpdateWorkTaskRequest(
    string? Title,
    string? Description,
    Guid? StrategicObjectiveId,
    Guid? AssignedRoleId,
    Guid? AssignedWorkerId,
    int? Status,
    int? Priority,
    DateTimeOffset? DueDate,
    bool? RequiresApproval,
    IReadOnlyList<CSweet.WorkManagement.Contracts.WorkItemMentionInput>? Mentions = null);
