namespace CSweet.Compute.Contracts;

public sealed record ComputeProviderWakeHint(Guid EventId);
public static class ComputeProviderWakeTransport
{
    public const string Path = "/api/compute/providers/wakes";
}
