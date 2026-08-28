namespace CSweet.Contracts.Security;

public sealed record SecurityEventQuery(
    string? Cursor = null,
    int Limit = 50,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Category = null,
    string? Direction = null,
    string? Outcome = null,
    string? ActorKind = null,
    string? Search = null);

public sealed record SecurityEventPageResponse(
    IReadOnlyList<SecurityEventSummaryResponse> Items,
    string? NextCursor);

public sealed record SecurityEventSummaryResponse(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Category,
    string Direction,
    string Outcome,
    string EventType,
    string ActorKind,
    string ActorDisplayName,
    string? TargetDisplayName,
    string? Summary,
    string? CorrelationId,
    string IntegrityStatus,
    string EntityType,
    Guid? EntityId);

public sealed class SecurityEventDetailResponse
{
    public Guid Id { get; init; }
    public long Sequence { get; init; }
    public Guid OrganizationId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public Guid TraceId { get; init; }
    public Guid? ParentEventId { get; init; }
    public IReadOnlyList<Guid> ChildEventIds { get; init; } = [];
    public string? ExternalMessageId { get; init; }
    public string? ExternalRequestId { get; init; }
    public string? CorrelationId { get; init; }
    public string ActorKind { get; init; } = string.Empty;
    public bool IdentityVerified { get; init; }
    public Guid? ActorApplicationUserId { get; init; }
    public Guid? ActorOrganizationUserId { get; init; }
    public string? ActorDisplayName { get; init; }
    public string? ActorAgentId { get; init; }
    public Guid? ActorInstallationId { get; init; }
    public Guid? ActorRuntimeInstanceId { get; init; }
    public Guid? ActorTickId { get; init; }
    public string? ActorSessionId { get; init; }
    public string? ActorPackageId { get; init; }
    public string? ActorPackageVersion { get; init; }
    public string? RemotePeer { get; init; }
    public string? TargetKind { get; init; }
    public string? TargetDisplayName { get; init; }
    public string? TargetAgentId { get; init; }
    public Guid? TargetInstallationId { get; init; }
    public string? TargetSessionId { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public string? Summary { get; init; }
    public string? MetadataJson { get; init; }
    public string? ContentType { get; init; }
    public string? PayloadPreview { get; init; }
    public string? PayloadSha256 { get; init; }
    public long? PayloadSize { get; init; }
    public bool PayloadTruncated { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PreviousRecordHash { get; init; }
    public string? RecordHash { get; init; }
    public string IntegrityStatus { get; init; } = string.Empty;
}
