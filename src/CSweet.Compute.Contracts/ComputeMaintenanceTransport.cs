namespace CSweet.Compute.Contracts;

public static class ComputeMaintenanceTransport
{
    public const string Path = "/api/compute/providers/maintenance";
    public const int MaximumRequestBytes = 256 * 1024;
    public const int MaximumAcknowledgementBytes = 4096;
}

public sealed record ComputeMaintenanceAcknowledgement(Guid EventId, string EventDigest);
