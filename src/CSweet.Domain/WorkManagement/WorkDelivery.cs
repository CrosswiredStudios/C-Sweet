namespace CSweet.Domain.WorkManagement;

public sealed class WorkTaskIntegrationReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ItemExecutionId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid RepositoryId { get; set; }
    public long ScopeRevision { get; set; }
    public string SourceCommitSha { get; set; } = "";
    public string TargetCommitSha { get; set; } = "";
    public string CandidateCommitSha { get; set; } = "";
    public string Status { get; set; } = "Prepared";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Project release scope is independent of the team's sprint lifecycle.</summary>
public sealed class WorkDeliveryPlan
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ManagerOrganizationUserId { get; set; }
    public string Status { get; set; } = "Draft";
    public long Revision { get; set; } = 1;
    public long ScopeRevision { get; set; } = 1;
    public string EpicItemIdsJson { get; set; } = "[]";
    public string BranchesJson { get; set; } = "[]";
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<WorkDeliveryExecution> Executions { get; set; } = [];
    public ICollection<WorkDeliveryFinding> Findings { get; set; } = [];
}

public sealed class WorkDeliveryFinding
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ExecutionId { get; set; }
    public string CandidateDigest { get; set; } = "";
    public string FindingDigest { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string RemediationTaskIdsJson { get; set; } = "[]";
    public string? ResolutionEvidence { get; set; }
    public Guid? ResolvedByOrganizationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public WorkDeliveryPlan? Plan { get; set; }
}

public sealed class WorkDeliveryExecution
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public Guid? WorkItemId { get; set; }
    public Guid BoardId { get; set; }
    public long ScopeRevision { get; set; }
    public long Revision { get; set; } = 1;
    public string Status { get; set; } = "WaitingForChildren";
    public string CurrentStageKey { get; set; } = string.Empty;
    public string? CandidateJson { get; set; }
    public string? AcceptanceJson { get; set; }
    public string? BlockedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public WorkDeliveryPlan? Plan { get; set; }
    public ICollection<WorkStageExecution> Stages { get; set; } = [];
    public ICollection<WorkDeliveryPromotion> Promotions { get; set; } = [];
}

public sealed class WorkDeliveryPromotion
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid RepositoryId { get; set; }
    public string SourceCommitSha { get; set; } = string.Empty;
    public string TargetCommitSha { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? MergeCommitSha { get; set; }
    public string? Error { get; set; }
    public long Revision { get; set; } = 1;
    public WorkDeliveryExecution? Execution { get; set; }
}

public sealed class WorkDeliveryMutationReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ActorId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestDigest { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
