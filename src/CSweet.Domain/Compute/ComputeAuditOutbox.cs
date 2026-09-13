namespace CSweet.Domain.Compute;

/// <summary>Immutable evidence captured with a compute mutation, awaiting sealed ledger delivery.</summary>
public sealed class ComputeAuditOutbox
{
    public Guid Id { get; set; }
    public string RequestJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
