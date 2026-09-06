namespace CSweet.Domain.Setup;

public static class AgentCapabilityBindingOrigins
{
    public const string Explicit = "Explicit";
    public const string AutomaticUnique = "AutomaticUnique";
    public const string VersionMigration = "VersionMigration";
}

public sealed class AgentCapabilityBinding
{
    public Guid Id { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public Guid RequesterInstallationId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string? DependencyId { get; set; }
    public string? ProviderPackageDigest { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public long GrantRevision { get; set; }
    public string Origin { get; set; } = AgentCapabilityBindingOrigins.Explicit;
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public AgentInstallation? RequesterInstallation { get; set; }
    public AgentInstallation? ProviderInstallation { get; set; }
}
