using CSweet.Domain.Core;
using CSweet.Domain.Security;

namespace CSweet.Domain.WorkManagement;

public enum WorkBoardColumnCategory
{
    ToDo,
    InProgress,
    Done,
    Cancelled
}

public enum WorkBoardWipPolicy
{
    Disabled,
    Warning,
    HardLimit
}

public enum WorkSprintStatus
{
    Planned,
    Active,
    Completed,
    Cancelled
}

public enum WorkAutomationExecutionStatus
{
    Pending,
    Succeeded,
    Skipped,
    Denied,
    Failed
}

public sealed class WorkBoard
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public ICollection<WorkBoardColumn> Columns { get; set; } = [];
    public ICollection<WorkSprint> Sprints { get; set; } = [];
    public ICollection<WorkAutomationRule> AutomationRules { get; set; } = [];
}

public sealed class WorkBoardColumn
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkBoardColumnCategory Category { get; set; }
    public int Position { get; set; }
    public WorkBoardWipPolicy WipPolicy { get; set; }
    public int? WipLimit { get; set; }

    public WorkBoard? Board { get; set; }
}

public sealed class WorkBoardUserPreference
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public bool IsFavorite { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }

    public WorkBoard? Board { get; set; }
}

public sealed class WorkSprint
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public WorkSprintStatus Status { get; set; } = WorkSprintStatus.Planned;
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public decimal? CapacityPoints { get; set; }
    public int? Sequence { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public WorkBoard? Board { get; set; }
}

public sealed class WorkItemDependency
{
    public Guid WorkItemId { get; set; }
    public Guid DependsOnWorkItemId { get; set; }
    public WorkTask? WorkItem { get; set; }
    public WorkTask? DependsOnWorkItem { get; set; }
}

public sealed class WorkDeliveryPipeline
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid DeveloperInstallationId { get; set; }
    public Guid QualityInstallationId { get; set; }
    public Guid DevelopmentColumnId { get; set; }
    public Guid QualityColumnId { get; set; }
    public Guid DoneColumnId { get; set; }
    public Guid RepositoryConnectionId { get; set; }
    public string BaseBranch { get; set; } = "main";
    public string MergeStrategy { get; set; } = "Squash";
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = "Disabled";
    public string Stage { get; set; } = "Idle";
    public Guid? ActiveSprintId { get; set; }
    public Guid? ActiveWorkItemId { get; set; }
    public int QualityCycle { get; set; }
    public string MergeStatus { get; set; } = "None";
    public string? SourcePullRequestUrl { get; set; }
    public string? SourceCommitSha { get; set; }
    public string? LastError { get; set; }
    public string? ResumeAction { get; set; }
    public int ConsecutiveInfrastructureFailures { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkQualityRun
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid QualityInstallationId { get; set; }
    public long AssignmentRevision { get; set; }
    public int QualityCycle { get; set; }
    public string SourceCommitSha { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public string ResultJson { get; set; } = "{}";
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkSprintSnapshot
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid SprintId { get; set; }
    public string SprintName { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public decimal? CapacityPoints { get; set; }
    public int CommittedItemCount { get; set; }
    public int CompletedItemCount { get; set; }
    public decimal CommittedPoints { get; set; }
    public decimal CompletedPoints { get; set; }
    public string ScopeJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkSprintMetricPoint
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid SprintId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int ScopeItemCount { get; set; }
    public int CompletedItemCount { get; set; }
    public decimal ScopePoints { get; set; }
    public decimal CompletedPoints { get; set; }
    public decimal RemainingPoints { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class WorkAutomationRule
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid AutomationIdentityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TriggerEventType { get; set; } = string.Empty;
    public Guid? ConditionColumnId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid TargetColumnId { get; set; }
    public bool IsEnabled { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public WorkBoard? Board { get; set; }
}

public sealed class WorkAutomationExecution
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid RuleId { get; set; }
    public Guid SourceActivityId { get; set; }
    public Guid WorkItemId { get; set; }
    public WorkAutomationExecutionStatus Status { get; set; }
    public string RequiredAction { get; set; } = string.Empty;
    public Guid? AuthorizingGrantId { get; set; }
    public long? AuthorizingGrantRevision { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    public WorkAutomationRule? Rule { get; set; }
}

public sealed class WorkSprintMutationReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public GrantSubjectKind ActorKind { get; set; }
    public Guid ActorSubjectId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkItemMutationReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkItemComment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkItemId { get; set; }
    public GrantSubjectKind AuthorKind { get; set; }
    public Guid AuthorSubjectId { get; set; }
    public string AuthorDisplayName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class WorkItemActivity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid WorkItemId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public GrantSubjectKind ActorKind { get; set; }
    public Guid ActorSubjectId { get; set; }
    public string ActorDisplayName { get; set; } = string.Empty;
    public Guid? AuthorizingGrantId { get; set; }
    public long? AuthorizingGrantRevision { get; set; }
    public string? IdempotencyKey { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
