using CSweet.Domain.Compute;

namespace CSweet.Compute.Contracts;

public enum ComputeMaintenancePhase { Requested, Observed, Failed }

/// <summary>Offline provider evidence, not a new grant or confirmed storage-teardown assertion.</summary>
public sealed record ComputeMaintenanceEvent(Guid EventId, Guid NodeId, Guid OrganizationId, string ProviderId,
    Guid EnvironmentId, Guid InstallationId, Guid ProvisionOperationId, long Generation, string SpecificationDigest,
    string? ResourceId, ComputePersistence Persistence, DateTimeOffset LeaseExpiresAt, string Action, long Attempt,
    ComputeMaintenancePhase Phase, bool PhysicalConfirmed, string? FailureCode,
    IReadOnlyList<ComputeActionAuthorization> ProvisionGrants, DateTimeOffset OccurredAt);
