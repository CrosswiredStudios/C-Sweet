namespace CSweet.Domain.Setup;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid? OrganizationId { get; set; }
    public string Category { get; set; } = "Domain";
    public string Direction { get; set; } = "Internal";
    public string Outcome { get; set; } = "Completed";
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Guid TraceId { get; set; }
    public Guid? ParentEventId { get; set; }
    public string? ExternalMessageId { get; set; }
    public string? ExternalRequestId { get; set; }
    public string? CorrelationId { get; set; }

    public string ActorKind { get; set; } = "Unknown";
    public bool IdentityVerified { get; set; }
    public Guid? ActorApplicationUserId { get; set; }
    public Guid? ActorOrganizationUserId { get; set; }
    public string? ActorDisplayName { get; set; }
    public string? ActorAgentId { get; set; }
    public Guid? ActorInstallationId { get; set; }
    public Guid? ActorRuntimeInstanceId { get; set; }
    public Guid? ActorTickId { get; set; }
    public string? ActorSessionId { get; set; }
    public string? ActorPackageId { get; set; }
    public string? ActorPackageVersion { get; set; }
    public string? RemotePeer { get; set; }

    public string? TargetKind { get; set; }
    public string? TargetDisplayName { get; set; }
    public string? TargetAgentId { get; set; }
    public Guid? TargetInstallationId { get; set; }
    public string? TargetSessionId { get; set; }

    public string? ContentType { get; set; }
    public string? PayloadPreview { get; set; }
    public string? PayloadSha256 { get; set; }
    public long? PayloadSize { get; set; }
    public bool PayloadTruncated { get; set; }
    public string? EvidenceSha256 { get; set; }
    public string? EmployeesJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public int IntegrityVersion { get; set; }
    public string? PreviousRecordHash { get; set; }
    public string? RecordHash { get; set; }
    public string? IntegritySeal { get; set; }
}
