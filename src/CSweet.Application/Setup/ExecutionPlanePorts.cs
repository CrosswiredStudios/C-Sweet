using CSweet.SatelliteOffice.Contracts.Workloads;

namespace CSweet.Application.Setup;

public enum IsolationWorkloadState
{
    Creating, Created, Starting, BootstrappingGuest, Running, Stopping, Stopped, Destroying, Destroyed, Failed
}

public enum IsolationTerminationReason
{
    None, Completed, Cancelled, StartFailed, GuestBootstrapFailed, LeaseExpired, RuntimeLimitExceeded,
    ResourceLimitExceeded, PolicyDenied, SecurityViolation, ProviderFailure, HostShutdown
}

public sealed record IsolationWorkloadHandle(string ProviderId, Guid WorkloadId, string ProviderInstanceId, WorkloadKind Kind);

public sealed record IsolationWorkloadStatus(
    IsolationWorkloadHandle Handle,
    IsolationWorkloadState State,
    IsolationTerminationReason TerminationReason,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? SanitizedError);

public interface IAgentArtifactStore
{
    Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default);
    Task<AgentArtifactReference> ImportAsync(Stream content, ArtifactImportDescriptor descriptor, CancellationToken cancellationToken = default);
}

public interface IAgentArtifactSigner
{
    string Sign(string artifactDigest, string provenanceJson);
    bool Verify(string artifactDigest, string provenanceJson, string signature);
}

public sealed record ArtifactImportDescriptor(
    string ExpectedDigest,
    long MaximumBytes,
    string FormatVersion,
    string OperatingSystem,
    string Architecture,
    string ProvenanceJson);

public interface IBuildProfileRegistry
{
    BuildProfileDescriptor Resolve(string runtimeType, string? targetFramework);
}

public sealed record BuildProfileDescriptor(
    string ProfileId,
    string Version,
    string RuntimeType,
    string GuestImageId,
    IReadOnlySet<string> AllowedPackageHosts,
    TimeSpan MaximumDuration);

public interface IGuestImageRegistry
{
    Task<GuestImageReference> ResolveAsync(GuestImageResolutionRequest request, CancellationToken cancellationToken = default);
}

public sealed record GuestImageResolutionRequest(
    string LogicalImageId,
    string? Version,
    string OperatingSystem,
    string Architecture,
    AgentTrustLevel TrustLevel,
    string BrokerProtocolVersion,
    string? PreferredProviderId = null,
    string? ExpectedDigest = null,
    string? RequiredCertificationSuiteVersion = null);

public sealed record BuilderArtifactResult(Guid WorkloadId, AgentArtifactReference Artifact, string OpaqueLocator);

public interface IBuilderArtifactResultStore
{
    Task<BuilderArtifactResult> WaitAsync(Guid workloadId, CancellationToken cancellationToken = default);
}

public interface IBuilderArtifactResultPublisher
{
    Task PublishAsync(BuilderArtifactResult result, CancellationToken cancellationToken = default);
}

public sealed class IsolationUnavailableException(string message) : Exception(message);
