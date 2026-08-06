namespace CSweet.AgentRuntime.Abstractions;

public interface IAgentIsolationProvider
{
    IsolationProviderDescriptor Descriptor { get; }

    Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    Task<IsolationWorkloadHandle> CreateAsync(
        IsolationWorkloadSpec workload,
        CancellationToken cancellationToken = default);

    Task StartAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default);

    Task<IsolationWorkloadStatus?> InspectAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        IsolationWorkloadHandle handle,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default);

    Task DestroyAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(
        IsolationWorkloadHandle handle,
        int maximumBytes,
        CancellationToken cancellationToken = default);
}

public interface IAgentIsolationProviderSelector
{
    Task<IsolationProviderSelection> SelectAsync(
        IsolationSelectionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeHostClient : IAgentIsolationProvider;

public interface IPlatformIsolationBackend : IAgentIsolationProvider;

/// <summary>
/// Contract boundary for a future remote runner. Remote execution is deliberately
/// not an implicit fallback: callers must select it as a separately certified provider.
/// </summary>
public interface IRemoteSecureRunnerClient : IAgentIsolationProvider;

public interface IAgentArtifactStore
{
    Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default);

    Task<AgentArtifactReference> ImportAsync(
        Stream content,
        ArtifactImportDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

public interface IAgentArtifactMediaStore
{
    Task EnsureReadOnlyMediaAsync(
        string digest,
        CancellationToken cancellationToken = default);
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
    GuestImageReference Resolve(
        string logicalImageId,
        string hostOperatingSystem,
        string hostArchitecture);
}

public sealed record BuilderArtifactResult(
    Guid WorkloadId,
    AgentArtifactReference Artifact,
    string OpaqueLocator);

public interface IBuilderArtifactResultStore
{
    Task<BuilderArtifactResult> WaitAsync(
        Guid workloadId,
        CancellationToken cancellationToken = default);
}

public interface IBuilderArtifactResultPublisher
{
    Task PublishAsync(
        BuilderArtifactResult result,
        CancellationToken cancellationToken = default);
}

public sealed class IsolationUnavailableException : Exception
{
    public IsolationUnavailableException(string message) : base(message) { }
}
