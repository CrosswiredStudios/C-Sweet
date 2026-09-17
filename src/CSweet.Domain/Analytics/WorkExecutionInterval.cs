namespace CSweet.Domain.Analytics;

/// <summary>Server-owned task focus for one authenticated delivery attempt.</summary>
public sealed class WorkExecutionContext
{
    public Guid Id { get; set; } // AgentWorkAttempt.Id
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public Guid AgentWorkItemId { get; set; }
    public Guid? RootWorkItemId { get; set; }
    public Guid? WorkItemId { get; set; }
    public long Revision { get; set; }
}

/// <summary>Non-overlapping effort segments. Only confirmed execution counts, never recovery latency.</summary>
public sealed class WorkExecutionInterval
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentWorkAttemptId { get; set; }
    public Guid? WorkItemId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public string AncestorWorkItemIdsJson { get; set; } = "[]";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ConfirmedThrough { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public string Provenance { get; set; } = "AuthenticatedAttempt";
}
