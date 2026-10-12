namespace CSweet.Domain.WorkManagement;

/// <summary>Business-owned instruction and reporting state, independent of runtime leases.</summary>
public sealed class TrackedDelegation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OriginalRequesterId { get; set; }
    public Guid AuthoredByOrganizationUserId { get; set; }
    public Guid InstructionAuthorId { get; set; }
    public Guid CoordinatorId { get; set; }
    public Guid RecipientId { get; set; }
    public Guid SourceConversationId { get; set; }
    public Guid SourceMessageId { get; set; }
    public string SourceChecksum { get; set; } = "";
    public Guid ReportingConversationId { get; set; }
    public Guid RecipientConversationId { get; set; }
    public Guid RecipientMessageId { get; set; }
    public Guid? ExecutionTurnId { get; set; }
    public Guid? ExecutionMessageId { get; set; }
    public Guid CommitmentItemId { get; set; }
    public Guid? WorkItemId { get; set; }
    public Guid? ParentDelegationId { get; set; }
    public string Instruction { get; set; } = "";
    public string IntendedOutcome { get; set; } = "";
    public string InterestedPartiesJson { get; set; } = "[]";
    public string? WorkContextJson { get; set; }
    public string Status { get; set; } = "AwaitingAcknowledgement";
    public bool IsDirective { get; set; }
    public long Revision { get; set; } = 1;
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastSubstantiveAt { get; set; }
    public DateTimeOffset? AttentionRaisedAt { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public DateTimeOffset? TrackingCancelledAt { get; set; }
}

public sealed class WorkTrackingSubscription
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid FollowerId { get; set; }
    public Guid? DelegationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UnfollowedAt { get; set; }
    public DateTimeOffset? ProcessedThrough { get; set; }
}

/// <summary>Explicitly shared report, with immutable source identity and revision.</summary>
public sealed class WorkTrackingUpdate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid? DelegationId { get; set; }
    public Guid AuthorId { get; set; }
    public long SourceRevision { get; set; }
    public string Kind { get; set; } = "Progress";
    public string Summary { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public Guid? ReportMessageId { get; set; }
}

public sealed class WorkTrackingOperationReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ActorId { get; set; }
    public string Operation { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid WorkItemId { get; set; }
    public Guid? UpdateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
