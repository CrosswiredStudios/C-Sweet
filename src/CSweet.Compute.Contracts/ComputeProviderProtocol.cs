using CSweet.Domain.Compute;

namespace CSweet.Compute.Contracts;

public sealed record ComputeProviderCapabilities(
    string ProviderId, HashSet<string> TemplateIds, HashSet<string> Actions,
    HashSet<ComputeNetworkMode> NetworkModes, bool SupportsGpu, bool SupportsPersistentEnvironments);

/// <summary>
/// Platform-authenticated operation. The provider independently verifies its signed authorization,
/// scope, specification digest, generation and expiry inside the privileged boundary.
/// A resource ID alone never authorizes a mutation. Replays must reconcile the same operation.
/// </summary>
public sealed record ComputeProviderOperation(
    Guid OperationId, Guid EnvironmentId, Guid OrganizationId, Guid InstallationId,
    Guid NodeId, long Generation, string Action, string SpecificationDigest,
    DateTimeOffset ExpiresAt, string SignedAuthorization);

public sealed record ComputeProviderObservation(
    Guid EnvironmentId, long Generation, string? ResourceId, ComputeLifecycleState State,
    DateTimeOffset ObservedAt, bool TeardownConfirmed, string? FailureCode = null);

