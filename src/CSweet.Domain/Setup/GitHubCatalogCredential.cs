namespace CSweet.Domain.Setup;

/// <summary>
/// The platform's GitHub credential for reading the agent and plugin catalog: repository metadata,
/// manifests and published release bundles. One row. It is never given to an agent or a runtime.
/// </summary>
public sealed class GitHubCatalogCredential
{
    public Guid Id { get; set; }
    public string ProtectedToken { get; set; } = string.Empty;
    public string AccountLogin { get; set; } = string.Empty;
    public string AccountType { get; set; } = "User";
    public Guid ConfiguredByUserId { get; set; }
    public DateTimeOffset ConfiguredAt { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}
