namespace CSweet.Domain.Analytics;

/// <summary>Append-only lifecycle evidence, committed with the source mutation.</summary>
public sealed class WorkLifecycleEvent
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ResourceId { get; set; }
    public string ResourceKind { get; set; } = "WorkItem";
    public string? PreviousStatus { get; set; }
    public string Status { get; set; } = "";
    public long SourceRevision { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Provenance { get; set; } = "ExecutionTiming";
}
