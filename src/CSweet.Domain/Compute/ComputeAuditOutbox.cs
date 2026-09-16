namespace CSweet.Domain.Compute;

/// <summary>Immutable evidence captured with a platform mutation, awaiting sealed ledger delivery.</summary>
public sealed class ComputeAuditOutbox
{
    public Guid Id { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string? SourceEntityType { get; set; }
    public Guid? SourceEntityId { get; set; }
    public byte[]? ProtectedRequest { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
