namespace CSweet.Domain.Core;

/// <summary>Immutable, content-free evidence of an explicitly authorized enrichment retry.</summary>
public sealed class MemoryCaptureRetryReceipt
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid OperationId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public int PreviousGeneration { get; set; }
    public int RetryGeneration { get; set; }
    public int PreviousAttempts { get; set; }
    public bool ReusesAcceptedExtraction { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
