namespace CSweet.Domain.Compute;

public sealed class ComputeRequestReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
