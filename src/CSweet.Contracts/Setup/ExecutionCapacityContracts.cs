namespace CSweet.Contracts.Setup;

public sealed record ExecutionCapacityOnboardingResponse(
    string SelectedMode,
    bool IsReady,
    int ReadyNodeCount,
    int PendingNodeCount,
    bool CanInstallLocalNode,
    string LocalOperatingSystem,
    string LocalArchitecture,
    string Summary,
    ExecutionEnrollmentResponse? ActiveEnrollment,
    IReadOnlyList<ExecutionNodeSummaryResponse> Nodes,
    IReadOnlyList<ExecutionCapacityCheckResponse> Checks,
    IReadOnlyList<ExecutionCapacityCheckResponse>? LocalPrerequisites = null,
    OfficePackageLinksResponse? Packages = null,
    LocalExecutionNodeProvisioningProgressResponse? LocalProvisioning = null);

public sealed record LocalExecutionNodeProvisioningProgressResponse(
    Guid JobId,
    string Platform,
    string State,
    string PhaseKey,
    string PhaseDisplayName,
    string Message,
    int PercentComplete,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    bool RequiresRestart,
    string? ErrorCode,
    string? ErrorMessage,
    int? EstimatedRemainingMinimumSeconds = null,
    int? EstimatedRemainingMaximumSeconds = null);

public sealed record OfficePackageLinksResponse(
    string ReleaseManifestUrl,
    string? WindowsPackageOverrideUrl,
    string? LinuxPackageOverrideUrl,
    string? MacOsPackageOverrideUrl,
    string? ControlPlaneUrl);

public sealed record SelectExecutionOnboardingModeRequest(string Mode);
public sealed record InstallLocalExecutionNodeRequest(string EnrollmentToken);

public sealed record ExecutionEnrollmentResponse(
    Guid Id,
    Guid ExecutionPoolId,
    string Status,
    DateTimeOffset ExpiresAt,
    string? EnrollmentToken = null,
    string? EnrollmentReceipt = null);

public sealed record ExecutionNodeSummaryResponse(
    Guid Id,
    Guid ExecutionPoolId,
    string Name,
    string MachineName,
    string OperatingSystem,
    string Architecture,
    string NodeVersion,
    string ProtocolVersion,
    string Status,
    string CertificateThumbprint,
    DateTimeOffset? CertificateExpiresAt,
    int AllocatableCpuCount,
    int AllocatableMemoryMb,
    int AllocatableDiskMb,
    int MaximumConcurrentWorkloads,
    DateTimeOffset? LastHeartbeatAt,
    IReadOnlyList<ExecutionNodeProviderResponse> Providers,
    IReadOnlyDictionary<string, string> Labels,
    bool IsLocalMachine = false);

public sealed record ExecutionNodeProviderResponse(
    string ProviderId,
    string ProviderVersion,
    string BrokerProtocolVersion,
    string GuestImageDigest,
    string CertificationSuiteVersion,
    string CertificationEvidenceDigest,
    DateTimeOffset CertifiedAt,
    DateTimeOffset? CertificationExpiresAt,
    bool SupportsBuilderWorkloads,
    bool SupportsRuntimeWorkloads,
    bool IsAvailable,
    string? UnavailableReason);

public sealed record ExecutionCapacityCheckResponse(
    string Key,
    string DisplayName,
    string Status,
    string Message,
    string? Remediation = null);

public sealed record ExecutionCapacityActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    ExecutionCapacityOnboardingResponse Status,
    ExecutionEnrollmentResponse? Enrollment = null);

public sealed record ExecutionPoolResponse(
    Guid Id,
    string Name,
    bool IsDefaultBuildPool,
    bool IsDefaultRuntimePool,
    bool IsEnabled,
    int MaximumActiveWorkloads,
    int ReadyNodeCount,
    int NodeCount,
    int ActiveAssignmentCount,
    IReadOnlyDictionary<string, string> RequiredLabels,
    IReadOnlyList<string> AllowedBusinessIds);
