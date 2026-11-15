namespace CSweet.Domain.WorkManagement;

public sealed class WorkIssueCase
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string ObligationKey { get; set; } = "";
    public int Episode { get; set; } = 1;
    public Guid? PreviousIssueId { get; set; }
    public string Statement { get; set; } = "";
    public string Detail { get; set; } = "";
    public string BriefTitle { get; set; } = "";
    public string BriefSummary { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string Phase { get; set; } = "Working";
    public Guid AccountableEmployeeId { get; set; }
    public string NextAction { get; set; } = "";
    public string SourceJson { get; set; } = "{}";
    public string ScopeKeysJson { get; set; } = "[]";
    public string LinksJson { get; set; } = "[]";
    public string? ResolutionEvidence { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Durable dispatch intent. Session creation is idempotent by this record's ID.</summary>
public sealed class WorkIssueCommission
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid InitiatorEmployeeId { get; set; }
    public Guid InitiatorInstallationId { get; set; }
    public Guid TargetEmployeeId { get; set; }
    public string IssueIdsJson { get; set; } = "[]";
    public string SourceJson { get; set; } = "{}";
    public string Question { get; set; } = "";
    public string SupersededSessionIdsJson { get; set; } = "[]";
    public string Status { get; set; } = "Pending";
    public Guid? SessionId { get; set; }
    public string? Failure { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public long Revision { get; set; } = 1;
}
