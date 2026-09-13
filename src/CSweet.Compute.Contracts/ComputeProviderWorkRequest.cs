namespace CSweet.Compute.Contracts;

/// <summary>Short-lived node proof for bounded work discovery or a single dispatch claim.</summary>
public sealed record ComputeProviderWorkRequest(Guid OrganizationId, Guid NodeId, string ProviderId,
    Guid RequestId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, Guid? OperationId = null, Guid? AfterOperationId = null, long? ResultSequence = null, string? ResultDigest = null);
public sealed record SignedComputeProviderWorkRequest(string KeyId, string PayloadJson, string SignatureBase64);

public sealed record ComputeProviderWorkPage(IReadOnlyList<Guid> OperationIds, Guid? NextAfterOperationId);
public sealed record ComputePublicationPermission(Guid OperationId, bool Allowed);
public static class ComputeWorkTransport
{
    public const string PublicationAccessPath = "/api/compute/providers/publication-access";
    public const string DiscoveryPath = "/api/compute/providers/work";
    public const string ClaimPath = "/api/compute/providers/claims";
    public const string ReceiptPath = "/api/compute/providers/result-receipts";
    public const int MaximumRequestBytes = 64 * 1024;
}
