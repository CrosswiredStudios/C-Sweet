namespace CSweet.Domain.Setup;

public sealed class PluginConnection
{
    public Guid Id { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string DeclarationId { get; set; } = string.Empty;
    public string ProviderProfile { get; set; } = string.Empty;
    public PluginConnectionStatus Status { get; set; }
    public string GrantedScopesJson { get; set; } = "[]";
    public string? ExternalAccountId { get; set; }
    public string? ExternalAccountName { get; set; }
    public string? BoundResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public AgentInstallation? AgentInstallation { get; set; }
}

public enum PluginConnectionStatus
{
    Pending,
    Connected,
    ReauthorizationRequired,
    Revoked
}

public sealed class PluginOAuthAttempt
{
    public Guid Id { get; set; }
    public Guid AgentInstallationId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public string ConnectionDeclarationId { get; set; } = string.Empty;
    public string ScopeSetId { get; set; } = string.Empty;
    public string StateHash { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>Administrator-managed OAuth provider metadata with an encrypted client secret.</summary>
public sealed class PluginProviderProfile
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string? RevocationEndpoint { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ProtectedClientSecret { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
