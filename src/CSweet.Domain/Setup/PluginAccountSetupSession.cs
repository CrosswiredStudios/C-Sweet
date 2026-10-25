namespace CSweet.Domain.Setup;

/// <summary>One human-initiated, resumable account consent/import; never shared installation setup.</summary>
public sealed class PluginAccountSetupSession
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public Guid AccountGroupId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? ReplacesConnectionId { get; set; }
    public long? ReplacesAuthorizationRevision { get; set; }
    public string PackageDigest { get; set; } = string.Empty;
    public string ProfileHash { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public string ScopeSetIdsJson { get; set; } = "[]";
    public string RequestedScopesJson { get; set; } = "[]";
    public string StateHash { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string Status { get; set; } = "AwaitingConsent";
    public string? FailureCode { get; set; }
    public string ObservedResourcesJson { get; set; } = "[]";
    public string? PageToken { get; set; }
    public string SeenPageTokensJson { get; set; } = "[]";
    public string? VerifiedSubject { get; set; }
    public string? VerifiedName { get; set; }
    public string? IdentityAuthority { get; set; }
    public string? RetiredCredentialKey { get; set; }
    public int PagesImported { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? PolicyAcceptedAt { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class PluginAccountOperator
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public Guid? AccountGroupId { get; set; }
    public Guid GrantedByApplicationUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
