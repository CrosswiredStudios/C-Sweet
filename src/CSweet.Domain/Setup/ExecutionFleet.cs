namespace CSweet.Domain.Setup;

public enum ExecutionOnboardingMode
{
    None = 0,
    Local = 1,
    Remote = 2
}

public enum ExecutionNodeStatus
{
    PendingApproval = 0,
    Ready = 1,
    Offline = 2,
    Draining = 3,
    Revoked = 4
}

public enum ExecutionEnrollmentStatus
{
    Available = 0,
    Claimed = 1,
    Approved = 2,
    Revoked = 3,
    Expired = 4
}

public enum LocalOfficeSetupSessionStatus
{
    Created = 0,
    Redeemed = 1,
    Connected = 2,
    Ready = 3,
    Failed = 4,
    Expired = 5,
    Revoked = 6,
    RecoveryRequired = 7,
    RemovalInProgress = 8,
    Removed = 9
}

public enum ExecutionAssignmentStatus
{
    Pending = 0,
    Assigned = 1,
    Starting = 2,
    Running = 3,
    Stopping = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
    Fenced = 8
}

public enum ExecutionWorkloadKind
{
    Builder = 0,
    Runtime = 1,
    ToolchainBuild = 2
}

public sealed class ExecutionPool
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefaultBuildPool { get; set; }
    public bool IsDefaultRuntimePool { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int MaximumActiveWorkloads { get; set; } = 100;
    public string RequiredLabelsJson { get; set; } = "{}";
    public string AllowedBusinessIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ExecutionNode> Nodes { get; set; } = [];
}

public sealed class ExecutionNode
{
    public Guid Id { get; set; }
    public Guid ExecutionPoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string NodeVersion { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = "1.0";
    public ExecutionNodeStatus Status { get; set; } = ExecutionNodeStatus.PendingApproval;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public string CertificateSerialNumber { get; set; } = string.Empty;
    public DateTimeOffset? CertificateExpiresAt { get; set; }
    public string CertificateSigningRequestPem { get; set; } = string.Empty;
    public string? IssuedCertificateBase64 { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public int AllocatableCpuCount { get; set; }
    public int AllocatableMemoryMb { get; set; }
    public int AllocatableDiskMb { get; set; }
    public int MaximumConcurrentWorkloads { get; set; }
    public long SessionEpoch { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastAssignedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? DrainingAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ExecutionPool? ExecutionPool { get; set; }
    public ICollection<ExecutionNodeProvider> Providers { get; set; } = [];
    public ICollection<ExecutionWorkloadAssignment> Assignments { get; set; } = [];
}

public sealed class ExecutionNodeProvider
{
    public Guid Id { get; set; }
    public Guid ExecutionNodeId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderVersion { get; set; } = string.Empty;
    public string BrokerProtocolVersion { get; set; } = "1.0";
    public string GuestImageDigest { get; set; } = string.Empty;
    public string CertificationSuiteVersion { get; set; } = string.Empty;
    public string CertificationEvidenceDigest { get; set; } = string.Empty;
    public DateTimeOffset CertifiedAt { get; set; }
    public DateTimeOffset? CertificationExpiresAt { get; set; }
    public bool SupportsBuilderWorkloads { get; set; }
    public bool SupportsRuntimeWorkloads { get; set; }
    public bool SupportsToolchainBuildWorkloads { get; set; }
    public bool IsAvailable { get; set; }
    public string? UnavailableReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ExecutionNode? ExecutionNode { get; set; }
}

public sealed class ExecutionNodeEnrollment
{
    public Guid Id { get; set; }
    public Guid ExecutionPoolId { get; set; }
    public Guid? ExecutionNodeId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string? ReceiptHash { get; set; }
    public ExecutionEnrollmentStatus Status { get; set; } = ExecutionEnrollmentStatus.Available;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ExecutionPool? ExecutionPool { get; set; }
    public ExecutionNode? ExecutionNode { get; set; }
}

public sealed class LocalOfficeSetupSession
{
    public Guid Id { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ExecutionNodeEnrollmentId { get; set; }
    public Guid? ExecutionNodeId { get; set; }
    public string HandoffSecretHash { get; set; } = string.Empty;
    public string MachineBindingHash { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = "windows";
    public string Architecture { get; set; } = string.Empty;
    public string ControlPlaneOrigin { get; set; } = string.Empty;
    public string? ControlPlaneCertificateSha256 { get; set; }
    public string PresetKey { get; set; } = "balanced";
    public int AllocatableCpuCount { get; set; }
    public int AllocatableMemoryMb { get; set; }
    public int AllocatableDiskMb { get; set; }
    public int MaximumConcurrentWorkloads { get; set; }
    public LocalOfficeSetupSessionStatus Status { get; set; } = LocalOfficeSetupSessionStatus.Created;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string RecoveryAction { get; set; } = "none";
    public bool RecoveryCanReconnect { get; set; }
    public string? SetupReceiptHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AdministratorApprovalRequestedAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ExecutionNodeEnrollment? ExecutionNodeEnrollment { get; set; }
    public ExecutionNode? ExecutionNode { get; set; }
}

public sealed class ExecutionWorkloadAssignment
{
    public Guid Id { get; set; }
    public Guid ExecutionPoolId { get; set; }
    public Guid? ExecutionNodeId { get; set; }
    public Guid? AgentBuildJobId { get; set; }
    public Guid? AgentRuntimeInstanceId { get; set; }
    public Guid? DeliveryBuildId { get; set; }
    public string? BusinessId { get; set; }
    public ExecutionWorkloadKind WorkloadKind { get; set; }
    public ExecutionAssignmentStatus Status { get; set; } = ExecutionAssignmentStatus.Pending;
    public string ProviderId { get; set; } = string.Empty;
    public string GuestImageDigest { get; set; } = string.Empty;
    public string? ArtifactDigest { get; set; }
    public string SpecificationJson { get; set; } = "{}";
    public string SpecificationDigest { get; set; } = string.Empty;
    public string AssignmentTokenHash { get; set; } = string.Empty;
    public string? ArtifactGrantTransferHash { get; set; }
    public DateTimeOffset? ArtifactGrantInUseUntil { get; set; }
    public DateTimeOffset? ArtifactGrantConsumedAt { get; set; }
    public long FencingEpoch { get; set; } = 1;
    public int Attempt { get; set; } = 1;
    public int ReservedCpuCount { get; set; }
    public int ReservedMemoryMb { get; set; }
    public int ReservedDiskMb { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureCode { get; set; }
    public string? SanitizedFailure { get; set; }
    public string? ProviderInstanceId { get; set; }
    public string? ResultArtifactLocator { get; set; }
    public string? ResultArtifactDigest { get; set; }
    public string? ResultArtifactSignature { get; set; }
    public string? ResultArtifactFormatVersion { get; set; }
    public string? ResultArtifactOperatingSystem { get; set; }
    public string? ResultArtifactArchitecture { get; set; }
    public string? ResultLogExcerpt { get; set; }

    public ExecutionPool? ExecutionPool { get; set; }
    public ExecutionNode? ExecutionNode { get; set; }
    public AgentBuildJob? AgentBuildJob { get; set; }
    public AgentRuntimeInstance? AgentRuntimeInstance { get; set; }

    public bool IsActive => Status is ExecutionAssignmentStatus.Pending or
        ExecutionAssignmentStatus.Assigned or ExecutionAssignmentStatus.Starting or
        ExecutionAssignmentStatus.Running or ExecutionAssignmentStatus.Stopping;
}
