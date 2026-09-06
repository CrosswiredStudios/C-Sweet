namespace CSweet.Domain.Setup;

public sealed class ConnectorProfileApproval
{
    public Guid Id { get; set; }
    public Guid ConnectorInstallationId { get; set; }
    public Guid ApprovedByApplicationUserId { get; set; }
    public string PackageDigest { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ConnectorExecution
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequesterInstallationId { get; set; }
    public Guid ConnectorInstallationId { get; set; }
    public Guid ConnectionId { get; set; }
    public long GrantRevision { get; set; }
    public string PackageDigest { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public string PlanHash { get; set; } = string.Empty;
    public string PlanJson { get; set; } = "{}";
    public string Status { get; set; } = "Prepared";
    public Guid? ApprovalId { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long Revision { get; set; }
}
