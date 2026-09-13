namespace CSweet.Domain.Compute;

/// <summary>Durable mutation intent and result. Provider retries reuse this identity and generation.</summary>
public sealed class ComputeOperation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public long Generation { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestDigest { get; set; }
    public string Action { get; set; } = "";
    public string AuthorityJson { get; set; } = "[]";
    public string? WorkloadJson { get; set; }
    public string? ResultJson { get; set; }
    public string TemplateJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public string? FailureCode { get; set; }
    public long LastResultSequence { get; set; }
    public string? LastResultDigest { get; set; }
    public Guid? DispatchLeaseId { get; set; }
    public DateTimeOffset? DispatchLeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;
}

/// <summary>Serializes all resource admissions for a subject, including across grants/providers.</summary>
public sealed class ComputeAdmission
{
    public Guid InstallationId { get; set; }
    public Guid OrganizationId { get; set; }
    public long Revision { get; set; } = 1;
}
