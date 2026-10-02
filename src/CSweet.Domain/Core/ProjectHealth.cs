namespace CSweet.Domain.Core;

public sealed class ProjectHealthState
{
    public Guid WorkstreamId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProducerEmployeeId { get; set; }
    public DateTimeOffset LastProgressAt { get; set; }
    public DateTimeOffset NextReviewAt { get; set; }
    public bool HasActiveWork { get; set; }
    public string? WaitingReason { get; set; }
    public long Revision { get; set; } = 1;
}

/// <summary>Transactional source wake and immutable, sanitized diagnostic snapshot.</summary>
public sealed class ProjectHealthSignal
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public bool MeaningfulProgress { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class ProjectIncident
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid ProducerEmployeeId { get; set; }
    public Guid CurrentRecipientId { get; set; }
    public Guid? AffectedWorkItemId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string Reason { get; set; } = "";
    public string Facts { get; set; } = "";
    public string LikelyCause { get; set; } = "Not established.";
    public string MissingEvidence { get; set; } = "";
    public string RecommendedAction { get; set; } = "Inspect the correlated failure evidence and restore the blocked dependency.";
    public string EvidenceJson { get; set; } = "[]";
    public string HistoryJson { get; set; } = "[]";
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset LastProgressAt { get; set; }
    public DateTimeOffset? EscalateAt { get; set; }
    public string Disposition { get; set; } = "Escalate";
    public DateTimeOffset? ReviewAt { get; set; }
    public string? ActionReference { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class ProjectIncidentReceipt
{
    public Guid IncidentId { get; set; }
    public Guid ActorId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
}

public sealed class ProjectIncidentDelivery
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public Guid RecipientId { get; set; }
    public Guid SenderId { get; set; }
    public long Revision { get; set; }
    public string Markdown { get; set; } = "";
    public Guid? ConversationId { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
