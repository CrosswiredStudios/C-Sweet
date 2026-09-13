namespace CSweet.Domain.Compute;

/// <summary>Durable application-owned preparation. No private provider keys are stored here.</summary>
public sealed class ComputeLocalSetup
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string State { get; set; } = "Pending";
    public long Revision { get; set; } = 1;
    public string? HandoffHash { get; set; }
    public DateTimeOffset? HandoffExpiresAt { get; set; }
    public string? TemplateId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ComputeAgentAccess
{
    public Guid Id { get; set; } // Installation ID: one default workspace scope per installation.
    public Guid OrganizationId { get; set; }
    public Guid SetupId { get; set; }
    public Guid WorkstreamId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? GrantsCreatedAt { get; set; }
}
