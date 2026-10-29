namespace CSweet.Infrastructure.Setup;

public sealed class PluginConnectionOptions
{
    public const string SectionName = "CSweet:PluginConnections";
    public string? PublicBaseUrl { get; set; }
    /// <summary>Trusted UI origin/base path for OAuth returns when the API and UI are hosted separately.</summary>
    public string? ReturnBaseUrl { get; set; }
    public ManagedPluginConnectionOptions Managed { get; set; } = new();
    /// <summary>Legacy diagnostic flag only. Cleanup readiness never gates installation or account consent.</summary>
    public bool AllowDevelopmentLifecyclePreview {get;set;}
    public Dictionary<string, OAuthProviderProfileOptions> Providers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ManagedPluginConnectionOptions
{
    public bool Enabled { get; set; }
    public string ServiceOrigin { get; set; } = string.Empty;
    public string ReturnUri { get; set; } = string.Empty;
    public string RegistrationPath { get; set; } = string.Empty;
    public string[] CustomerOwnedProfiles { get; set; } = [];
}

public sealed class OAuthProviderProfileOptions
{
    public string DisplayName { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string? RevocationEndpoint { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
