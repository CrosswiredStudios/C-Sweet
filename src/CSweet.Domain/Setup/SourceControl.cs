namespace CSweet.Domain.Setup;

public enum SourceControlProvider
{
    GitHub,
    GenericGit
}

public enum SourceControlConnectionMode
{
    ManagedGitHub,
    ExistingGitHub,
    GenericGitHttps,
    GenericGitSsh
}

public enum SourceControlConnectionStatus
{
    Pending,
    Connected,
    AttentionRequired,
    Disconnected
}

public enum SourceControlCredentialKind
{
    HttpsToken,
    SshPrivateKey
}

public enum SourceControlRepositoryStatus
{
    Provisioning,
    Ready,
    AttentionRequired,
    Archived,
    Disconnected
}

public enum RepositoryProvisioningStatus
{
    Pending,
    AwaitingApproval,
    Provisioning,
    Completed,
    Quarantined,
    Failed,
    Cancelled
}

public enum TeamMergeApprovalMode
{
    LeadAuthorizedAutoMerge,
    LeadAndAdministratorApproval
}

public enum SourceControlOnboardingStatus
{
    InProgress,
    AwaitingProvider,
    Completed,
    Cancelled
}

public enum SourceControlWorkspaceStatus
{
    Pending,
    Preparing,
    Ready,
    Published,
    Failed,
    Removed
}

public enum SourceControlPublicationStatus
{
    Published,
    AwaitingValidation,
    AwaitingLeadAuthorization,
    AwaitingAdministratorApproval,
    ReadyToMerge,
    Merged,
    BranchPublishedExternalMerge,
    Superseded,
    Failed
}

public enum SourceControlValidationStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Superseded
}

public enum SourceControlMergeStatus
{
    Pending,
    AwaitingApproval,
    Ready,
    Merging,
    Merged,
    Superseded,
    Failed,
    Cancelled
}

public enum SourceControlApprovalKind
{
    RepositoryProvisioning,
    Merge
}

public enum PlatformGitHubAppKind
{
    SourceAccess,
    Provisioner
}

public enum PlatformGitHubAppCredentialStatus
{
    Pending,
    Verified,
    Activating,
    Active,
    Failed,
    Superseded
}

public enum PlatformSourceControlSetupStatus
{
    InProgress,
    AwaitingGitHub,
    ReadyToActivate,
    Active,
    Cancelled,
    Expired
}

/// <summary>
/// Installation-wide GitHub App material. ProtectedPrivateKey is encrypted with the shared
/// C-Sweet Data Protection key ring and is never projected into an API response.
/// </summary>
public sealed class PlatformGitHubAppCredential
{
    public Guid Id { get; set; }
    public PlatformGitHubAppKind Kind { get; set; }
    public string OwnerLogin { get; set; } = string.Empty;
    public long AppId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string AppSlug { get; set; } = string.Empty;
    public string InstallUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ProtectedClientSecret { get; set; } = string.Empty;
    public string ProtectedPrivateKey { get; set; } = string.Empty;
    public string ProtectionVersion { get; set; } = "v1";
    public PlatformGitHubAppCredentialStatus Status { get; set; }
        = PlatformGitHubAppCredentialStatus.Pending;
    public string? FailureMessage { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A resumable, system-administrator-owned enterprise setup wizard.</summary>
public sealed class PlatformSourceControlSetupSession
{
    public Guid Id { get; set; }
    public Guid StartedByApplicationUserId { get; set; }
    public PlatformSourceControlSetupStatus Status { get; set; }
        = PlatformSourceControlSetupStatus.InProgress;
    public string CurrentStep { get; set; } = "organization";
    public string GitHubOrganization { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string ManifestCallbackUrl { get; set; } = string.Empty;
    public bool PrerequisitesConfirmed { get; set; }
    public bool SourceAccessPermissionsConfirmed { get; set; }
    public bool SourceAccessAppConfirmed { get; set; }
    public bool? ProvisionerRequested { get; set; }
    public bool ProvisionerPermissionsConfirmed { get; set; }
    public bool ProvisionerAppConfirmed { get; set; }
    public bool ActivationConfirmed { get; set; }
    public PlatformGitHubAppKind? PendingAppKind { get; set; }
    public string StateNonceHash { get; set; } = string.Empty;
    public DateTimeOffset? StateExpiresAt { get; set; }
    public Guid? SourceAccessCredentialId { get; set; }
    public Guid? ProvisionerCredentialId { get; set; }
    public string? LastError { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A business-owned provider account or generic Git host connection. This record contains
/// provider identifiers and an opaque secret reference only; it never contains secret material.
/// </summary>
public sealed class SourceControlConnection
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SourceControlProvider Provider { get; set; }
    public SourceControlConnectionMode Mode { get; set; }
    public SourceControlConnectionStatus Status { get; set; } = SourceControlConnectionStatus.Pending;
    public string ProviderAccountId { get; set; } = string.Empty;
    public string AccountLogin { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public long? SourceAccessInstallationId { get; set; }
    public long? ProvisionerInstallationId { get; set; }
    public string? AllowedHost { get; set; }
    public int? AllowedPort { get; set; }
    public string SshHostFingerprintsJson { get; set; } = "[]";
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastHealthError { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    public ICollection<SourceControlRepository> Repositories { get; set; } = [];
    public ICollection<RepositoryProvisioningPolicy> ProvisioningPolicies { get; set; } = [];
    public ICollection<SourceControlRepositoryTemplate> RepositoryTemplates { get; set; } = [];
    public ICollection<SourceControlCredential> Credentials { get; set; } = [];
}

/// <summary>
/// Core-owned encrypted generic Git credential. ProtectedPayload is write-only outside the
/// trusted credential service and is never projected into an API or agent capability response.
/// </summary>
public sealed class SourceControlCredential
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConnectionId { get; set; }
    public SourceControlCredentialKind Kind { get; set; }
    public string ProtectedPayload { get; set; } = string.Empty;
    public string ProtectionVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public SourceControlConnection? Connection { get; set; }
}

/// <summary>
/// A repository resolved by Core. Agents refer to work assignments, never provider locations.
/// </summary>
public sealed class SourceControlRepository
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ExternalRepositoryId { get; set; } = string.Empty;
    public string ProviderRepositoryKey { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CanonicalPath { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public bool IsPrivate { get; set; } = true;
    public bool IsManaged { get; set; }
    public SourceControlRepositoryStatus Status { get; set; } = SourceControlRepositoryStatus.Provisioning;
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastHealthError { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public SourceControlConnection? Connection { get; set; }
    public ICollection<TeamRepositoryPolicy> TeamPolicies { get; set; } = [];
}

public sealed class RepositoryProvisioningPolicy
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? DefaultTeamId { get; set; }
    public string NamePrefix { get; set; } = string.Empty;
    public string NamingPattern { get; set; } = string.Empty;
    public string ApprovedTemplatesJson { get; set; } = "[]";
    public int MaximumRepositories { get; set; }
    public bool RequiresManagerApproval { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public SourceControlConnection? Connection { get; set; }
}

/// <summary>
/// A Core-approved GitHub template. Agent requests contain only this record's opaque ID; provider
/// coordinates are resolved after authorization and never accepted from an agent payload.
/// </summary>
public sealed class SourceControlRepositoryTemplate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ExternalRepositoryId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public bool IsEnabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public SourceControlConnection? Connection { get; set; }
}

public sealed class RepositoryProvisioningRequest
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid RequestedByOrganizationUserId { get; set; }
    public Guid? RequestedByAgentInstallationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? ApprovalId { get; set; }
    public Guid? RepositoryId { get; set; }
    public Guid TemplateId { get; set; }
    public long PolicyRevision { get; set; }
    public string ProjectDisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public RepositoryProvisioningStatus Status { get; set; } = RepositoryProvisioningStatus.Pending;
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public SourceControlConnection? Connection { get; set; }
    public RepositoryProvisioningPolicy? Policy { get; set; }
    public SourceControlRepositoryTemplate? Template { get; set; }
    public SourceControlRepository? Repository { get; set; }
}

public sealed class SourceControlApproval
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public SourceControlApprovalKind Kind { get; set; }
    public Core.ApprovalStatus Status { get; set; } = Core.ApprovalStatus.Pending;
    public Guid RequestedByOrganizationUserId { get; set; }
    public Guid? RequestedByAgentInstallationId { get; set; }
    public Guid? ProvisioningRequestId { get; set; }
    public Guid? MergeJobId { get; set; }
    public Guid? DecidedByOrganizationUserId { get; set; }
    public string? DecisionComment { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class TeamRepositoryPolicy
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TeamId { get; set; }
    public Guid RepositoryId { get; set; }
    public bool IsPrimary { get; set; }
    public TeamMergeApprovalMode MergeApprovalMode { get; set; }
        = TeamMergeApprovalMode.LeadAuthorizedAutoMerge;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public SourceControlRepository? Repository { get; set; }
}

public sealed class SourceControlOnboardingSession
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid StartedByOrganizationUserId { get; set; }
    public SourceControlConnectionMode SelectedMode { get; set; }
    public SourceControlOnboardingStatus Status { get; set; } = SourceControlOnboardingStatus.InProgress;
    public string CurrentStep { get; set; } = string.Empty;
    public Guid? ConnectionId { get; set; }
    public string StateNonceHash { get; set; } = string.Empty;
    public string DraftJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Durable ticket-scoped workspace state. WorkspaceKey is an opaque GitHost identifier rather
/// than a host path and cannot be used to select a repository.
/// </summary>
public sealed class SourceControlWorkspace
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TeamId { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public Guid WorkItemId { get; set; }
    public long AssignmentRevision { get; set; }
    public string WorkspaceKey { get; set; } = string.Empty;
    public string BaseCommitSha { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public SourceControlWorkspaceStatus Status { get; set; } = SourceControlWorkspaceStatus.Pending;
    public string? LastError { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetainUntil { get; set; }

    public SourceControlRepository? Repository { get; set; }
}

public sealed class SourceControlPublication
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RepositoryId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public string TicketBranch { get; set; } = string.Empty;
    public string? PullRequestId { get; set; }
    public string? PullRequestUrl { get; set; }
    public SourceControlPublicationStatus Status { get; set; }
        = SourceControlPublicationStatus.Published;
    public string ChangedFilesJson { get; set; } = "[]";
    public string ValidationResultsJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public SourceControlWorkspace? Workspace { get; set; }
    public SourceControlRepository? Repository { get; set; }
}

public sealed class SourceControlValidation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid ValidatorAgentInstallationId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public SourceControlValidationStatus Status { get; set; } = SourceControlValidationStatus.Pending;
    public string ResultsJson { get; set; } = "[]";
    public string? FailureMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }

    public SourceControlPublication? Publication { get; set; }
}

public sealed class SourceControlMergeAuthorization
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid AuthorizedByOrganizationUserId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public long TeamPolicyRevision { get; set; }
    public string DecisionSignature { get; set; } = string.Empty;
    public DateTimeOffset AuthorizedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    public SourceControlPublication? Publication { get; set; }
}

public sealed class SourceControlMergeJob
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid LeadAuthorizationId { get; set; }
    public Guid? AdministratorApprovalId { get; set; }
    public string ExpectedHeadSha { get; set; } = string.Empty;
    public TeamMergeApprovalMode ApprovalMode { get; set; }
    public SourceControlMergeStatus Status { get; set; } = SourceControlMergeStatus.Pending;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? MergeCommitSha { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public SourceControlPublication? Publication { get; set; }
    public SourceControlMergeAuthorization? LeadAuthorization { get; set; }
}
