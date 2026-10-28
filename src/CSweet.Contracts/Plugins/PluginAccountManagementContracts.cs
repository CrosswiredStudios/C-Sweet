namespace CSweet.Contracts.Plugins;

public sealed record PluginAccountManagementView(Guid InstallationId, string Name, string Version, string PackageDigest, string BuildStatus,
    bool CanConnect, string? ReadinessMessage, PluginAccountManagement Declaration,
    IReadOnlyList<PluginConnectionScopeSet> ScopeSets, IReadOnlyList<PluginManagedAccount> Accounts,
    IReadOnlyList<PluginAccountSetupView> SetupSessions, bool CanAdminister)
{
    public string? PluginId { get; init; }
    public string? OAuthRedirectUri { get; init; }
    public string ConnectionMode { get; init; } = "CustomerOwned";
    public PluginAdministrationView? Administration { get; init; }
    public IReadOnlyList<PluginDeletionView> Deletions { get; init; } = [];
    public IReadOnlyList<PluginCleanupReview> CleanupReviews {get;init;}=[];
}
public sealed record PluginCleanupReview(Guid AccountId,Guid OrganizationId,Guid EmployeeId,Guid EpisodeId);
public sealed record PluginManagedAccount(Guid Id, string Name, string State, long Revision, bool ProviderVerified,
    IReadOnlyList<PluginManagedResource> Resources, IReadOnlyList<PluginManagedAuthorization> Authorizations,
    IReadOnlyList<PluginBusinessAccountGrantView> BusinessGrants);
public sealed record PluginManagedResource(Guid Id, string ExternalId, string Name, string? Handle, bool Available);
public sealed record PluginManagedAuthorization(Guid Id, string State, IReadOnlyList<string> GrantedScopeSets, bool CanReconnect)
{
    public IReadOnlyList<Guid> ResourceIds { get; init; } = [];
}
public sealed record PluginAccountSetupView(Guid Id, string Status, int ImportedCount, string? FailureCode, DateTimeOffset ExpiresAt);
public sealed record PluginBusinessAccountGrantView(Guid Id, Guid OrganizationId, string Scope, IReadOnlyList<Guid> ResourceIds,
    IReadOnlyList<string> PermissionIds, long Revision, bool Revoked = false);
public sealed record ConnectPluginAccountRequest(IReadOnlyList<string> ScopeSetIds, string PolicyVersion, bool AcceptPolicies, string PackageDigest,
    Guid? ReconnectConnectionId = null);
public sealed record BeginPluginAccountResponse(Guid SetupId, string AuthorizationUrl, DateTimeOffset ExpiresAt);
public sealed record CompleteManagedPluginReturnRequest(Guid AttemptId, string Handoff);
public sealed record ManagedPluginReturnResponse(Guid InstallationId, Guid SetupId);
public sealed record ManagedConnectionStatus(bool Enabled, bool Paired, string ServiceOrigin, string ReturnUri)
{
    public IReadOnlyList<string> CustomerOwnedProfiles { get; init; } = [];
}
public sealed record ManagedConnectionPairing(string ServiceOrigin, string PublicKey, string KeyId, string ReturnUri);
public sealed record CompleteManagedConnectionPairing(Guid DeploymentId, string TenantId, long Revision);
public sealed record SavePluginAccountGrantRequest(Guid OrganizationId, string Scope, IReadOnlyList<Guid> ResourceIds,
    IReadOnlyList<string> PermissionIds, bool IncludeFutureResources, long? ExpectedRevision, string PackageDigest);
public sealed record PluginAccountImpactView(long AccountRevision, string ReviewHash, IReadOnlyList<string> Businesses, IReadOnlyList<string> Agents,
    bool ProviderRevocationMayAffectOtherAuthorizations)
{
    public int RecordedCopies {get;init;}
}
public sealed record DisconnectPluginAccountRequest(long AccountRevision, string ReviewHash);
public sealed record PluginAccountOperatorView(Guid Id, Guid UserId, string Name, Guid? AccountId, long Revision, bool Revoked = false);
public sealed record PluginOperatorCandidate(Guid Id, string Name);
public sealed record SavePluginOperatorRequest(Guid UserId, Guid? AccountId, long? ExpectedRevision, string PackageDigest);
public sealed record PluginAdministrationView(bool Enabled, bool Retired, bool RemovalPending, long Revision,
    IReadOnlyList<PluginAccountOperatorView> Operators, IReadOnlyList<PluginOperatorCandidate> Candidates)
{
    public string? RepositoryUrl {get;init;}
    public Guid? PreviousInstallationId {get;init;}
    public bool Staged {get;init;}
    public bool DevelopmentPreview {get;init;}
    public IReadOnlyList<string> MissingDeletionStores {get;init;}=[];
}
public sealed record PluginPackageReview(string PackageDigest,string ManifestJson,string BusinessId);
public sealed record PluginDeletionView(Guid AccountId, string Status, DateTimeOffset TargetAt, DateTimeOffset Deadline,
    bool HostCopiesDeleted, bool AllCopiesDeleted, int PendingExternalCopies);
public sealed record PluginInstallationImpact(string ReviewHash, IReadOnlyList<string> Businesses, IReadOnlyList<string> Agents, int Accounts);
public sealed record PluginInstallationControlRequest(string Action, string ReviewHash, long ExpectedRevision);
