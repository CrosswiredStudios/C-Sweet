using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Core;

public sealed record CreateWorkTaskRequest(
    [Required] string Title,
    string Description,
    Guid? StrategicObjectiveId,
    Guid? AssignedRoleId,
    Guid? AssignedWorkerId,
    int Status,
    int Priority,
    DateTimeOffset? DueDate,
    bool RequiresApproval,
    Guid? BoardId = null,
    IReadOnlyList<CSweet.WorkManagement.Contracts.WorkItemMentionInput>? Mentions = null);
