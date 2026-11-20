namespace CSweet.Domain.Compute;

/// <summary>Durable demand from a hired installation. A request confers no placement or execution authority.</summary>
public sealed class OfficeComputeCapacityRequest
{
    public Guid InstallationId { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public const string WaitingReason = "office_compute_capacity_unavailable";
}
