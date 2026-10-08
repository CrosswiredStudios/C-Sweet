namespace CSweet.Domain.Core;

/// <summary>Immutable, content-free evidence of inputs observed by a leased extraction attempt.</summary>
public sealed class MemoryExtractionInputReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid JobId { get; set; }
    public Guid LeaseToken { get; set; }
    public int RetryGeneration { get; set; }
    public string EvidenceJson { get; set; } = "";
    public string ReceiptHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public MemoryCaptureOutboxItem? Job { get; set; }
}
