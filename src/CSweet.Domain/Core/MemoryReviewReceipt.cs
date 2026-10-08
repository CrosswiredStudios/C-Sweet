namespace CSweet.Domain.Core;

/// <summary>Content-free, immutable evidence of one authenticated memory-review operation.</summary>
public sealed class MemoryReviewReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public string RecordKind { get; set; } = "Claim";
    public Guid MemoryId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public long PreviousRevision { get; set; }
    public Guid ResultMemoryId { get; set; }
    public long ResultRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
