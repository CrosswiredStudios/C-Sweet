namespace CSweet.Compute.Contracts;

public static class ComputeResultTransport
{
    public const string Path = "/api/compute/providers/results";
    public const int MaximumRequestBytes = 256 * 1024;
}

public enum ComputeResultDisposition { Recorded, Superseded, Completed }

public sealed record ComputeResultAcknowledgement(Guid OperationId, long Sequence, string PayloadDigest, bool Applied,
    ComputeResultDisposition Disposition = ComputeResultDisposition.Recorded);
