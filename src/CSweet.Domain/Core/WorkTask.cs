using CSweet.Domain.WorkManagement;

namespace CSweet.Domain.Core;

/// <summary>
/// Represents a business work task. Named WorkTask to avoid conflict with System.Threading.Tasks.Task.
/// </summary>
public sealed class WorkTask
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? BoardId { get; set; }
    public Guid? BoardColumnId { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? ParentWorkTaskId { get; set; }
    public Guid? StrategicObjectiveId { get; set; }
    public Guid? AssignedRoleId { get; set; }
    public Guid? AssignedWorkerId { get; set; }
    public Guid? AssignedEmployeeId { get; set; }
    public Guid? AssignedAgentInstallationId { get; set; }
    public Guid? AccountableOrganizationUserId { get; set; }
    public Guid? CreatedByOrganizationUserId { get; set; }
    public Guid? SourceConversationId { get; set; }
    public Guid? SourceMessageId { get; set; }
    public string? PersonalTodoResultSummary { get; set; }
    public string? PersonalTodoBlockReason { get; set; }
    public Guid? PersonalTodoClaimEventId { get; set; }
    public DateTimeOffset? PersonalTodoClaimExpiresAt { get; set; }
    public string? PersonalTodoIdempotencyKey { get; set; }
    public long? IdentifierSequence { get; set; }
    public string? Identifier { get; set; }
    public string? DevelopmentBriefJson { get; set; }
    public string? DeliverySpecificationJson { get; set; }
    public string? QualityBriefJson { get; set; }
    public bool IsQaTrackingDefect { get; set; }
    public string? QualityFindingFingerprint { get; set; }
    public int QualityCycle { get; set; }
    public string MergeStatus { get; set; } = "None";
    public string? MergeCommitSha { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public Guid? MergeQualityRunId { get; set; }
    public Guid? MergeAuthorizationGrantId { get; set; }
    public long? MergeAuthorizationGrantRevision { get; set; }
    public long AssignmentRevision { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorkItemKind Kind { get; set; } = WorkItemKind.Task;
    public WorkTaskStatus Status { get; set; }
    public WorkTaskPriority Priority { get; set; }
    public decimal? EstimatePoints { get; set; }
    public long BoardRank { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? DueDate { get; set; }
    public bool RequiresApproval { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Organization? Organization { get; set; }
    public CSweet.Domain.WorkManagement.WorkBoard? Board { get; set; }
    public CSweet.Domain.WorkManagement.WorkBoardColumn? BoardColumn { get; set; }
    public CSweet.Domain.WorkManagement.WorkSprint? Sprint { get; set; }
    public WorkTask? ParentWorkTask { get; set; }
    public ICollection<WorkTask> ChildWorkTasks { get; set; } = [];
    public ICollection<WorkItemDependency> Dependencies { get; set; } = [];
    public ICollection<WorkItemDependency> Dependents { get; set; } = [];
    public StrategicObjective? StrategicObjective { get; set; }
    public Role? AssignedRole { get; set; }
    public Worker? AssignedWorker { get; set; }
    public OrganizationUser? AssignedEmployee { get; set; }
    public CSweet.Domain.Setup.AgentInstallation? AssignedAgentInstallation { get; set; }
    public OrganizationUser? AccountableOrganizationUser { get; set; }
    public OrganizationUser? CreatedByOrganizationUser { get; set; }
    public ICollection<CSweet.Domain.WorkManagement.WorkItemStageAssignment> StageAssignments { get; set; } = [];
}
