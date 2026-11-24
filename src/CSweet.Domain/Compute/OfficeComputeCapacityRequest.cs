namespace CSweet.Domain.Compute;

/// <summary>Durable demand for an assigned project workspace. A request confers no placement or execution authority.</summary>
public sealed class OfficeComputeCapacityRequest
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public const string WaitingReason = "office_compute_capacity_unavailable";
}
