namespace CSweet.Domain.Compute;

/// <summary>Durable node-scoped wake hint. It never grants execution or replaces a current-state read.</summary>
public sealed class ComputeProviderWake
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid NodeId { get; set; }
    public string ProviderId { get; set; } = "";
    public Guid OperationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public long Revision { get; set; } = 1;
}
