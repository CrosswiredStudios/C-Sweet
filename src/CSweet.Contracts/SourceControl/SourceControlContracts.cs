namespace CSweet.Contracts.SourceControl;

public sealed record SourceControlDashboardResponse(
    IReadOnlyList<SourceControlConnectionSummary> Connections,
    IReadOnlyList<SourceControlRepositorySummary> Repositories,
    SourceControlOnboardingSummary? ActiveOnboarding,
    SourceControlPlatformReadiness PlatformReadiness,
    bool CanManageSourceControl);

public sealed record SourceControlPlatformReadiness(
    bool ExistingGitHubAvailable,
    bool ManagedGitHubAvailable,
    string? UserMessage,
    string ConfigurationMode = "Unconfigured");

public sealed record PlatformSourceControlSetupResponse(
    SourceControlPlatformReadiness Readiness,
    PlatformSourceControlSetupSessionResponse? Session);

public sealed record PlatformSourceControlSetupSessionResponse(
    Guid SessionId,
    string Status,
    string CurrentStep,
    string GitHubOrganization,
    string PublicBaseUrl,
    bool PrerequisitesConfirmed,
    bool SourceAccessPermissionsConfirmed,
    bool SourceAccessAppConfirmed,
    bool? ProvisionerRequested,
    bool ProvisionerPermissionsConfirmed,
    bool ProvisionerAppConfirmed,
    bool ActivationConfirmed,
    PlatformGitHubAppSummary? SourceAccessApp,
    PlatformGitHubAppSummary? ProvisionerApp,
    string? LastError,
    DateTimeOffset ExpiresAt,
    long Revision);

public sealed record PlatformGitHubAppSummary(
    Guid CredentialId,
    string Kind,
    string OwnerLogin,
    long AppId,
    string AppName,
    string AppSlug,
    string InstallUrl,
    string Status,
    long Revision,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ActivatedAt,
    string? FailureMessage);

public sealed record StartPlatformSourceControlSetupRequest(
    string PublicBaseUrl,
    string? ManifestCallbackUrl = null);
public sealed record ConfirmPlatformOrganizationRequest(
    string OrganizationLogin,
    bool PrerequisitesConfirmed,
    long ExpectedRevision);
public sealed record ConfirmPlatformAppReviewRequest(bool Confirmed, long ExpectedRevision);
public sealed record ChoosePlatformProvisionerRequest(bool EnableProvisioner, long ExpectedRevision);
public sealed record ConfirmPlatformAppRequest(bool Confirmed, long ExpectedRevision);
public sealed record ActivatePlatformSourceControlRequest(bool Confirmed, long ExpectedRevision);
public sealed record CancelPlatformSourceControlSetupRequest(bool Confirmed, long ExpectedRevision);
public sealed record PlatformGitHubManifestLaunchResponse(
    string PostUrl,
    string ManifestJson,
    DateTimeOffset ExpiresAt);

public sealed record SourceControlConnectionSummary(
    Guid Id,
    string Name,
    string Provider,
    string Mode,
    string AccountLogin,
    string AccountType,
    string Status,
    bool SourceAccessConnected,
    bool ProvisionerConnected,
    int RepositoryCount,
    DateTimeOffset? LastVerifiedAt,
    string? HealthMessage,
    long Revision);

public sealed record SourceControlRepositorySummary(
    Guid Id,
    Guid ConnectionId,
    string Name,
    string CanonicalPath,
    string DefaultBranch,
    string Status,
    bool IsPrivate,
    bool IsManaged,
    DateTimeOffset? LastVerifiedAt,
    string? HealthMessage);

public sealed record SourceControlOnboardingSummary(
    Guid SessionId,
    Guid? ConnectionId,
    string Mode,
    string Status,
    string CurrentStep,
    DateTimeOffset ExpiresAt);

public sealed record StartSourceControlOnboardingRequest(string Mode);

public sealed record StartSourceControlOnboardingResponse(
    Guid SessionId,
    string Mode,
    string CurrentStep,
    string AuthorizationUrl,
    DateTimeOffset ExpiresAt);

public sealed record CompleteGitHubAppInstallationRequest(
    string State,
    long InstallationId,
    string AppKind);

public sealed record CompleteGitHubAppInstallationResponse(
    Guid SessionId,
    Guid ConnectionId,
    string AccountLogin,
    string CurrentStep,
    string? NextAuthorizationUrl,
    bool InstallationSetupComplete);

public sealed record AvailableSourceControlRepository(
    string RepositoryId,
    string Name,
    string CodeProjectPath,
    string MainVersion,
    bool IsPrivate,
    bool IsTemplate);

public sealed record SelectExistingCodeProjectsRequest(
    IReadOnlyList<string> RepositoryIds);

public sealed record ConfigureManagedCodeProjectsRequest(
    IReadOnlyList<string> TemplateRepositoryIds,
    string NamePrefix,
    int MaximumProjects,
    bool RequiresManagerApproval,
    Guid DefaultTeamId,
    long? ExpectedPolicyRevision);

public sealed record ManagedCodeProjectPolicyResponse(
    Guid PolicyId,
    Guid ConnectionId,
    IReadOnlyList<Guid> ApprovedTemplateIds,
    string NamePrefix,
    int MaximumProjects,
    bool RequiresManagerApproval,
    Guid DefaultTeamId,
    long Revision);

public sealed record DecideSourceControlApprovalRequest(
    bool Approved,
    string? Feedback,
    long ExpectedRevision);

public sealed record SourceControlApprovalDecisionResponse(
    Guid ApprovalId,
    string Kind,
    string Status,
    Guid? ProvisioningRequestId,
    Guid? MergeJobId,
    DateTimeOffset DecidedAt,
    long Revision);
