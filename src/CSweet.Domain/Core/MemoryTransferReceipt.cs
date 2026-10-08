namespace CSweet.Domain.Core;

/// <summary>Immutable, content-free result of an authenticated transfer operation.</summary>
public sealed class MemoryTransferReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid TargetEmployeeId { get; set; }
    public Guid PackageId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? AppliedEpisodeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
