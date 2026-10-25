namespace CSweet.Contracts.Plugins;

public sealed record PluginAccountManagementView(Guid InstallationId, string Name, string Version, string PackageDigest, string BuildStatus,
    bool CanConnect, string? ReadinessMessage, PluginAccountManagement Declaration,
    IReadOnlyList<PluginConnectionScopeSet> ScopeSets, IReadOnlyList<PluginManagedAccount> Accounts,
    IReadOnlyList<PluginAccountSetupView> SetupSessions, bool CanAdminister);
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
public sealed record SavePluginAccountGrantRequest(Guid OrganizationId, string Scope, IReadOnlyList<Guid> ResourceIds,
    IReadOnlyList<string> PermissionIds, bool IncludeFutureResources, long? ExpectedRevision, string PackageDigest);
public sealed record PluginAccountImpactView(long AccountRevision, string ReviewHash, IReadOnlyList<string> Businesses, IReadOnlyList<string> Agents,
    bool ProviderRevocationMayAffectOtherAuthorizations);
public sealed record DisconnectPluginAccountRequest(long AccountRevision, string ReviewHash);
