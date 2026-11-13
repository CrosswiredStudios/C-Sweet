namespace CSweet.Compute.Contracts;

/// <summary>Short-lived node proof for bounded work discovery or a single dispatch claim.</summary>
public sealed record ComputeProviderWorkRequest(Guid OrganizationId, Guid NodeId, string ProviderId,
    Guid RequestId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, Guid? OperationId = null, Guid? AfterOperationId = null, long? ResultSequence = null, string? ResultDigest = null,
    int? InputOffset = null, Guid? DispatchId = null)
{
    /// <summary>SHA-256 of an uploaded evidence part; set only on evidence-part requests, with InputOffset and DispatchId.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? InputSha256 { get; init; }
}
public sealed record SignedComputeProviderWorkRequest(string KeyId, string PayloadJson, string SignatureBase64);

public sealed record ComputeProviderWorkPage(IReadOnlyList<Guid> OperationIds, Guid? NextAfterOperationId,
    ComputeProviderRetirement? Retirement = null);
public sealed record ComputePublicationPermission(Guid OperationId, bool Allowed);
public static class ComputeWorkTransport
{
    public const string BuildInputPath = "/api/compute/providers/build-input";
    public const string PublicationAccessPath = "/api/compute/providers/publication-access";
    public const string DependencyAccessPath = "/api/compute/providers/dependency-access";
    public const string DiscoveryPath = "/api/compute/providers/work";
    public const string ClaimPath = "/api/compute/providers/claims";
    public const string ReceiptPath = "/api/compute/providers/result-receipts";
    public const string EvidencePartPath = "/api/compute/providers/evidence-parts";
    public const int MaximumRequestBytes = 64 * 1024;
    /// <summary>One base64 part plus its signed envelope.</summary>
    public const int MaximumEvidencePartRequestBytes = 2 * 1024 * 1024;
}
