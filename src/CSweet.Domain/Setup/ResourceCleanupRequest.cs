namespace CSweet.Domain.Setup;

/// <summary>Host-owned deletion outbox. Deliberately has no cascading foreign keys.</summary>
public sealed class ResourceCleanupRequest
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid ResourceId { get; set; }
    public Guid BusinessId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
