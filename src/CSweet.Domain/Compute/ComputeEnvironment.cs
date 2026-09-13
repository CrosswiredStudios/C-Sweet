namespace CSweet.Domain.Compute;

/// <summary>A logical desired environment survives individual provider placements and agent restarts.</summary>
public sealed class ComputeEnvironment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string DesiredEnvironmentKey { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public string SpecificationJson { get; set; } = "{}";
    public ComputePersistence Persistence { get; set; }
    public ComputeDesiredState DesiredState { get; set; } = ComputeDesiredState.Running;
    public ComputeLifecycleState State { get; set; } = ComputeLifecycleState.Requested;
    public Guid? ProviderNodeId { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderResourceId { get; set; }
    public long Generation { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public int AttemptCount { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public DateTimeOffset? TeardownConfirmedAt { get; set; }

    // Failed, expired or stopped does not prove physical resources have been released.
    public bool HoldsReservation => TeardownConfirmedAt is null;

    public bool ShouldRequestAutomaticDestruction(DateTimeOffset now, bool ownerDeleted) =>
        Persistence == ComputePersistence.Ephemeral && (ownerDeleted || LeaseExpiresAt <= now) &&
        DesiredState != ComputeDesiredState.Destroyed && TeardownConfirmedAt is null;
}
