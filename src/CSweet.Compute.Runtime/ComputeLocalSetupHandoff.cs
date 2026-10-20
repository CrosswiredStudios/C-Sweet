using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Host-owned installer handoff shared by its writer and reader; never an agent input.</summary>
public sealed record ComputeLocalSetupHandoff(int Version, Guid SetupId, Guid OrganizationId, string Secret,
    string CoreOrigin, string CoreCertificateSha256, ComputeSigningIdentity ControlPlaneKey, string RepositoryRoot,
    Guid? AttemptId = null, ComputeProviderRetirement? Retirement = null);
