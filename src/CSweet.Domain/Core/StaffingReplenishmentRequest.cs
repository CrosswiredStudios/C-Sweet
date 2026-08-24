namespace CSweet.Domain.Core;

public enum StaffingReplenishmentRequestStatus
{
    Pending,
    Approved,
    RevisionRequested,
    Rejected
}

public sealed class StaffingReplenishmentRequestRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequesterOrganizationUserId { get; set; }
    public Guid RequesterInstallationId { get; set; }
    public Guid ManagerOrganizationUserId { get; set; }
    public Guid SourceResourceChangeRequestId { get; set; }
    public Guid TeamId { get; set; }
    public Guid ConversationId { get; set; }
    public string GapsJson { get; set; } = "[]";
    public string OperationalImpact { get; set; } = string.Empty;
    public string InterimControlsJson { get; set; } = "[]";
    public string DecisionFingerprint { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public StaffingReplenishmentRequestStatus Status { get; set; }
    public string? DecisionComment { get; set; }
    public string? DecisionIdempotencyKey { get; set; }
    public Guid? DecidedByOrganizationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
