namespace CSweet.Infrastructure.Setup;

public sealed class PluginConnectionOptions
{
    public const string SectionName = "CSweet:PluginConnections";
    public string? PublicBaseUrl { get; set; }
    public Dictionary<string, OAuthProviderProfileOptions> Providers { get; set; } = new(StringComparer.Ordinal);
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
