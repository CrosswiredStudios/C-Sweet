namespace CSweet.Domain.Setup;

/// <summary>Server-owned policy. An agent proposal is inactive until its exact document revision is approved by an owner.</summary>
public sealed class WebPreviewGrantRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public string PolicyJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public Guid ApprovalProposalId { get; set; }
    public Guid ApprovalArtifactId { get; set; }
    public Guid ApprovalRevisionId { get; set; }
    public string ApprovalContentDigest { get; set; } = "";
    public Guid RequestedByOrganizationUserId { get; set; }
    public Guid? ApprovedByOrganizationUserId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public long Revision { get; set; } = 1;
    public long ReservedCpuSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class WebPreviewJobRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public Guid GrantId { get; set; }
    public long GrantRevision { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid BuildId { get; set; }
    public Guid? WebHostId { get; set; }
    public string ManifestJson { get; set; } = "{}";
    public string ManifestDigest { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public string Phase { get; set; } = "Requested";
    public string? AccessReference { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastAccessAt { get; set; }
    public long Revision { get; set; } = 1;
}
