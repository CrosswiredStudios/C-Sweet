namespace CSweet.Domain.Setup;

/// <summary>An enterprise-owned grouping, not an OAuth credential or a business grant.</summary>
public sealed class PluginAccountGroup
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ProviderProfile { get; set; } = string.Empty;
    public PluginAccountIdentityKind IdentityKind { get; set; } = PluginAccountIdentityKind.Administrative;
    public string? VerifiedSubject { get; set; }
    public string? IdentityAuthority { get; set; }
    public PluginAccountState State { get; set; } = PluginAccountState.ConnectionRequired;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PluginAccountIdentityKind { Administrative, ProviderSubject }
public enum PluginAccountState { ConnectionRequired, Connected, Disconnecting, Disconnected }

/// <summary>Resource identity obtained from a reviewed authenticated discovery operation.</summary>
public sealed class PluginAccountResource
{
    public Guid Id { get; set; }
    public Guid AccountGroupId { get; set; }
    public Guid PluginInstallationId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string ExternalResourceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Handle { get; set; }
    public bool IsAvailable { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Provider proof that this authorization can access this resource; never inferred from labels.</summary>
public sealed class PluginAccountResourceAuthorization
{
    public Guid ResourceId { get; set; }
    public Guid ConnectionId { get; set; }
    public bool IsAvailable { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset VerifiedAt { get; set; }
}

public enum PluginAccountGrantScope { SelectedResources, WholeAccount }

/// <summary>Independent, revisioned business access. Revocation must not revoke shared provider tokens.</summary>
public sealed class PluginAccountBusinessGrant
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public Guid AccountGroupId { get; set; }
    public Guid OrganizationId { get; set; }
    public PluginAccountGrantScope Scope { get; set; }
    public string SelectedResourceIdsJson { get; set; } = "[]";
    public string CapabilitiesJson { get; set; } = "[]";
    public string PackageDigest { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public Guid ApprovedByApplicationUserId { get; set; }
    public DateTimeOffset? FutureResourcesAcknowledgedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
