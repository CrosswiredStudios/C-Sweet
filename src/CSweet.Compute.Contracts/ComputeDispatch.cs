using CSweet.Domain.Compute;

namespace CSweet.Compute.Contracts;

public enum ComputeDispatchMode { Execute, Observe }

public sealed record ComputeDispatchAuthorization(Guid DispatchId, Guid OperationId, Guid EnvironmentId,
    Guid OrganizationId, Guid InstallationId, Guid NodeId, string ProviderId, long Generation,
    string Action, ComputeDispatchMode Mode, string SpecificationDigest, string? TemplateDigest,
    string? ResourceId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, DateTimeOffset EnvironmentLeaseExpiresAt,
    IReadOnlyList<ComputeActionAuthorization> Grants, string? WorkloadDigest = null);

public sealed record SignedComputeDispatch(string KeyId, string PayloadJson, string SignatureBase64);
public sealed record ComputeDispatchPacket(SignedComputeDispatch Authorization, ComputeSpecification Specification,
    ComputeTemplate? Template, ComputeWorkload? Workload = null);
public sealed record ComputeSigningIdentity(string KeyId, string PublicKeyBase64);

