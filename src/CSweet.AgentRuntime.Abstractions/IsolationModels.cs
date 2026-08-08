namespace CSweet.AgentRuntime.Abstractions;

public enum IsolationAssurance
{
    None = 0,
    Process = 10,
    SharedKernelContainer = 20,
    HardwareVirtualMachine = 30,
    CertifiedHardwareVirtualMachine = 40,
    RemoteCertifiedHardwareVirtualMachine = 50
}

public enum AgentTrustLevel
{
    BuiltIn,
    PublisherTrusted,
    OrganizationApproved,
    UntrustedRepository,
    UntrustedMarketplace
}

public enum IsolationWorkloadKind
{
    Builder,
    Runtime
}

public enum IsolationWorkloadState
{
    Creating,
    Created,
    Starting,
    BootstrappingGuest,
    Running,
    Stopping,
    Stopped,
    Destroying,
    Destroyed,
    Failed
}

public enum IsolationTerminationReason
{
    None,
    Completed,
    Cancelled,
    StartFailed,
    GuestBootstrapFailed,
    LeaseExpired,
    RuntimeLimitExceeded,
    ResourceLimitExceeded,
    PolicyDenied,
    SecurityViolation,
    ProviderFailure,
    HostShutdown
}

public sealed record IsolationProviderCapabilities(
    IsolationAssurance Assurance,
    bool UsesDedicatedKernel,
    bool SupportsBrokerSocket,
    bool SupportsReadOnlyBaseDisk,
    bool SupportsReadOnlyArtifact,
    bool SupportsEphemeralWritableDisk,
    bool SupportsCpuLimits,
    bool SupportsMemoryLimits,
    bool SupportsDiskLimits,
    bool SupportsProcessLimits,
    bool SupportsNoNetworkDevice,
    bool SupportsSecureBoot,
    bool SupportsMeasuredOrVerifiedBoot)
{
    public bool Satisfies(IsolationCapabilityRequirements requirements) =>
        Assurance >= requirements.MinimumAssurance &&
        (!requirements.RequireDedicatedKernel || UsesDedicatedKernel) &&
        (!requirements.RequireBrokerSocket || SupportsBrokerSocket) &&
        (!requirements.RequireReadOnlyBaseDisk || SupportsReadOnlyBaseDisk) &&
        (!requirements.RequireReadOnlyArtifact || SupportsReadOnlyArtifact) &&
        (!requirements.RequireEphemeralWritableDisk || SupportsEphemeralWritableDisk) &&
        (!requirements.RequireCpuLimits || SupportsCpuLimits) &&
        (!requirements.RequireMemoryLimits || SupportsMemoryLimits) &&
        (!requirements.RequireDiskLimits || SupportsDiskLimits) &&
        (!requirements.RequireNoNetworkDevice || SupportsNoNetworkDevice) &&
        (!requirements.RequireSecureBoot || SupportsSecureBoot) &&
        (!requirements.RequireMeasuredOrVerifiedBoot || SupportsMeasuredOrVerifiedBoot);
}

public sealed record IsolationCapabilityRequirements(
    IsolationAssurance MinimumAssurance,
    bool RequireDedicatedKernel = true,
    bool RequireBrokerSocket = true,
    bool RequireReadOnlyBaseDisk = true,
    bool RequireReadOnlyArtifact = true,
    bool RequireEphemeralWritableDisk = true,
    bool RequireCpuLimits = true,
    bool RequireMemoryLimits = true,
    bool RequireDiskLimits = true,
    bool RequireNoNetworkDevice = true,
    bool RequireSecureBoot = false,
    bool RequireMeasuredOrVerifiedBoot = false);

public sealed record IsolationProviderDescriptor(
    string ProviderId,
    string DisplayName,
    string ProviderVersion,
    string HostOperatingSystem,
    string HostArchitecture,
    int Priority,
    IsolationProviderCapabilities Capabilities);

public sealed record IsolationProviderCertification(
    string ProviderId,
    string ProviderVersion,
    string HostOperatingSystem,
    string HostArchitecture,
    string GuestImageDigest,
    string BrokerProtocolVersion,
    string CertificationSuiteVersion,
    string EvidenceDigest,
    DateTimeOffset CertifiedAt,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsActiveAt(DateTimeOffset instant) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > instant);
}

public sealed record IsolationProviderProbeResult(
    IsolationProviderDescriptor Descriptor,
    bool IsAvailable,
    string? UnavailableReason,
    IsolationProviderCertification? Certification);

public sealed record IsolationResourceLimits(
    int VirtualCpuCount,
    int CpuPercent,
    int MemoryMegabytes,
    int WritableDiskMegabytes,
    int MaximumProcessCount,
    int MaximumLogBytes,
    TimeSpan MaximumDuration)
{
    public void Validate()
    {
        if (VirtualCpuCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(VirtualCpuCount));
        if (CpuPercent is < 1 or > 6400) throw new ArgumentOutOfRangeException(nameof(CpuPercent));
        if (MemoryMegabytes is < 128 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(MemoryMegabytes));
        if (WritableDiskMegabytes is < 64 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(WritableDiskMegabytes));
        if (MaximumProcessCount is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumProcessCount));
        if (MaximumLogBytes is < 1 or > 1_073_741_824) throw new ArgumentOutOfRangeException(nameof(MaximumLogBytes));
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
    }
}

public sealed record GuestImageReference(
    string ImageId,
    string Version,
    string Digest,
    string OperatingSystem,
    string Architecture);

public sealed record AgentArtifactReference(
    string Digest,
    string Signature,
    string FormatVersion,
    string OperatingSystem,
    string Architecture);

public sealed record BrokerChannelLease(
    Guid ChannelId,
    string ProtocolVersion,
    string BootToken,
    string ExpectedGuestImageDigest,
    string? ExpectedArtifactDigest,
    DateTimeOffset ExpiresAt);

public sealed record RepositoryDescriptor(
    string RepositoryUrl,
    string CommitSha,
    bool IncludeSubmodules,
    string BuildProfileId,
    string BuildProfileVersion);

public sealed record RuntimeAgentIdentity(
    Guid InstallationId,
    string BusinessId,
    Guid TickId);

public abstract record IsolationWorkloadSpec(
    Guid WorkloadId,
    IsolationWorkloadKind Kind,
    GuestImageReference GuestImage,
    IsolationResourceLimits ResourceLimits,
    BrokerChannelLease BrokerLease);

public sealed record BuilderWorkloadSpec(
    Guid WorkloadId,
    GuestImageReference GuestImage,
    IsolationResourceLimits ResourceLimits,
    BrokerChannelLease BrokerLease,
    RepositoryDescriptor Repository,
    long MaximumArtifactBytes)
    : IsolationWorkloadSpec(
        WorkloadId,
        IsolationWorkloadKind.Builder,
        GuestImage,
        ResourceLimits,
        BrokerLease);

public sealed record RuntimeWorkloadSpec(
    Guid WorkloadId,
    GuestImageReference GuestImage,
    IsolationResourceLimits ResourceLimits,
    BrokerChannelLease BrokerLease,
    AgentArtifactReference Artifact,
    RuntimeAgentIdentity Identity,
    IReadOnlyList<string> Entrypoint)
    : IsolationWorkloadSpec(
        WorkloadId,
        IsolationWorkloadKind.Runtime,
        GuestImage,
        ResourceLimits,
        BrokerLease);

public sealed record IsolationWorkloadHandle(
    string ProviderId,
    Guid WorkloadId,
    string ProviderInstanceId,
    IsolationWorkloadKind Kind);

public sealed record IsolationWorkloadStatus(
    IsolationWorkloadHandle Handle,
    IsolationWorkloadState State,
    IsolationTerminationReason TerminationReason,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? SanitizedError);

public sealed record IsolationLogChunk(
    DateTimeOffset OccurredAt,
    string Stream,
    ReadOnlyMemory<byte> Content,
    bool IsTruncated);

public sealed record IsolationSelectionRequest(
    AgentTrustLevel TrustLevel,
    IsolationCapabilityRequirements Requirements,
    string? GuestImageDigest,
    string BrokerProtocolVersion,
    string? PreferredProviderId = null);

public sealed record IsolationProviderSelection(
    IAgentIsolationProvider Provider,
    IsolationProviderProbeResult Probe);
