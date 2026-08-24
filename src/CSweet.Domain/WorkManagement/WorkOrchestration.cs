using CSweet.Domain.Core;
using CSweet.Domain.Setup;

namespace CSweet.Domain.WorkManagement;

public enum WorkOrchestrationStageType
{
    Queue,
    AgentExecution,
    ManualWork,
    MemberExecution,
    ManagerApproval,
    TrustedPlatformAction,
    Terminal
}

public enum WorkOrchestrationPrincipalKind
{
    Unassigned,
    Human,
    AgentInstallation,
    BoardManager,
    PlatformAction
}

public enum WorkSprintExecutionStatus
{
    Active,
    Paused,
    Completed,
    Cancelled
}

public enum WorkItemExecutionStatus
{
    Pending,
    Running,
    WaitingForHuman,
    WaitingForApproval,
    Blocked,
    Completed,
    Failed,
    Cancelled
}

public enum WorkStageExecutionStatus
{
    Pending,
    Dispatching,
    Running,
    WaitingForHuman,
    WaitingForApproval,
    Backoff,
    Blocked,
    Completed,
    Failed,
    Cancelled
}

public enum WorkExecutionAttemptStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Ignored
}

public sealed class WorkOrchestrationPolicy
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? PublishedRevisionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public WorkBoard? Board { get; set; }
    public ICollection<WorkOrchestrationPolicyRevision> Revisions { get; set; } = [];
}

public sealed class WorkOrchestrationPolicyRevision
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid PolicyId { get; set; }
    public int Revision { get; set; }
    public string Name { get; set; } = string.Empty;
    public string InitialStageKey { get; set; } = string.Empty;
    public string MergeMode { get; set; } = "ManagerApproval";
    public int GlobalConcurrencyLimit { get; set; } = 100;
    public int OrganizationConcurrencyLimit { get; set; } = 25;
    public int BoardConcurrencyLimit { get; set; } = 10;
    public int DefaultStageConcurrencyLimit { get; set; } = 5;
    public int DefaultAssigneeConcurrencyLimit { get; set; } = 1;
    public bool IsPublished { get; set; }
    public Guid? PublishedByOrganizationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public WorkOrchestrationPolicy? Policy { get; set; }
    public ICollection<WorkOrchestrationStage> Stages { get; set; } = [];
    public ICollection<WorkOrchestrationTransition> Transitions { get; set; } = [];
}

public sealed class WorkOrchestrationStage
{
    public Guid Id { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WorkOrchestrationStageType Type { get; set; }
    public Guid? ColumnId { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public string InputSchemaJson { get; set; } = "{}";
    public string OutputSchemaJson { get; set; } = "{}";
    public int TimeoutSeconds { get; set; } = 3600;
    public int? ConcurrencyLimit { get; set; }
    public int MaximumAttempts { get; set; } = 5;
    public int InitialRetryDelaySeconds { get; set; } = 10;
    public int MaximumRetryDelaySeconds { get; set; } = 300;
    public string? PlatformAction { get; set; }
    public bool IsSuccessfulTerminal { get; set; }

    public WorkOrchestrationPolicyRevision? PolicyRevision { get; set; }
}

public sealed class WorkOrchestrationTransition
{
    public Guid Id { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public string FromStageKey { get; set; } = string.Empty;
    public string OutcomeCode { get; set; } = string.Empty;
    public string ToStageKey { get; set; } = string.Empty;
    public int? MaximumTraversals { get; set; }

    public WorkOrchestrationPolicyRevision? PolicyRevision { get; set; }
}

public sealed class WorkItemStageAssignment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid WorkItemId { get; set; }
    public string StageKey { get; set; } = string.Empty;
    public WorkOrchestrationPrincipalKind PrincipalKind { get; set; }
    public Guid? OrganizationUserId { get; set; }
    public Guid? AgentInstallationId { get; set; }
    public string? PlatformAction { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public WorkTask? WorkItem { get; set; }
}

public sealed class WorkSprintExecution
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid SprintId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public Guid StartedByOrganizationUserId { get; set; }
    public WorkSprintExecutionStatus Status { get; set; }
    public string PolicySnapshotJson { get; set; } = "{}";
    public string AssignmentSnapshotJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    public ICollection<WorkItemExecution> Items { get; set; } = [];
}

public sealed class WorkItemExecution
{
    public Guid Id { get; set; }
    public Guid SprintExecutionId { get; set; }
    public Guid WorkItemId { get; set; }
    public string ItemIdentifier { get; set; } = string.Empty;
    public string CurrentStageKey { get; set; } = string.Empty;
    public int Traversal { get; set; }
    public WorkItemExecutionStatus Status { get; set; }
    public string? BlockedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public WorkSprintExecution? SprintExecution { get; set; }
    public WorkTask? WorkItem { get; set; }
    public ICollection<WorkStageExecution> Stages { get; set; } = [];
}

public sealed class WorkStageExecution
{
    public Guid Id { get; set; }
    public Guid ItemExecutionId { get; set; }
    public string StageKey { get; set; } = string.Empty;
    public WorkOrchestrationStageType StageType { get; set; }
    public int Traversal { get; set; }
    public WorkStageExecutionStatus Status { get; set; }
    public WorkOrchestrationPrincipalKind PrincipalKind { get; set; }
    public Guid? OrganizationUserId { get; set; }
    public Guid? AgentInstallationId { get; set; }
    public string? PlatformAction { get; set; }
    public string? LastOutcomeCode { get; set; }
    public string? LastSummary { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public WorkItemExecution? ItemExecution { get; set; }
    public ICollection<WorkExecutionAttempt> Attempts { get; set; } = [];
}

public sealed class WorkExecutionAttempt
{
    public Guid Id { get; set; }
    public Guid StageExecutionId { get; set; }
    public Guid? AgentWorkItemId { get; set; }
    public int Attempt { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public WorkExecutionAttemptStatus Status { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public WorkStageExecution? StageExecution { get; set; }
    public AgentWorkItem? AgentWorkItem { get; set; }
}

public sealed class WorkOrchestrationEvent
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid SprintExecutionId { get; set; }
    public Guid? ItemExecutionId { get; set; }
    public Guid? StageExecutionId { get; set; }
    public Guid? AttemptId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
