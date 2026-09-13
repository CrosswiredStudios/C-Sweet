using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

/// <summary>Installer-configured allocatable capacity, after host and platform overhead.</summary>
public sealed record ComputeProviderCapacity(int MaximumEnvironments, ComputeResources Resources)
{
    public bool IsValid => MaximumEnvironments is > 0 and <= 100000 && Resources is { IsValid: true };
}

/// <summary>A reservation is retained through failure, stop and uncertain teardown.</summary>
public sealed record ComputePhysicalReservation(Guid EnvironmentId, Guid InstallationId,
    string SpecificationDigest, ComputeResources Resources, ComputePersistence Persistence,
    DateTimeOffset LeaseExpiresAt, long Generation, Guid CurrentOperationId, bool DestroyRequested, string? ResourceId, bool LeaseExpired);

/// <summary>Only a trusted backend may bind an identity verified against physical ownership.</summary>
public sealed record ComputePhysicalOutcome<T>(T Result, string? ResourceId, ComputeLifecycleObservation? Observation = null);

/// <summary>Provider-local stop/destroy authority derived from the original recorded lease and persistence policy.</summary>
public sealed record ComputeLeaseEnforcement(Guid NodeId, Guid OrganizationId, string ProviderId,
    Guid EnvironmentId, Guid InstallationId, long Generation, string? ResourceId, string Action, DateTimeOffset LeaseExpiresAt);