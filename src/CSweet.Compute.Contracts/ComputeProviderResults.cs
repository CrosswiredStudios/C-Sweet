using CSweet.Domain.Compute;

namespace CSweet.Compute.Contracts;

public sealed record ComputeProviderResult(Guid OperationId, Guid EnvironmentId, Guid OrganizationId,
    Guid InstallationId, string ProviderId, Guid NodeId, long Generation, string Action,
    string SpecificationDigest, long Sequence, string? ResourceId, ComputeLifecycleState State,
    bool TeardownConfirmed, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, string? FailureCode = null, ComputeWorkloadResult? Workload = null);

public sealed record SignedComputeProviderResult(string KeyId, string PayloadJson, string SignatureBase64);

public sealed record ComputeNodeVerificationKey(string KeyId, Guid OrganizationId, Guid NodeId,
    string ProviderId, string SubjectPublicKeyInfoBase64);

