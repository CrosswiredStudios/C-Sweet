namespace CSweet.Domain.Setup;

public sealed class AgentCapabilityBinding
{
    public Guid Id { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public Guid RequesterInstallationId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public Guid ProviderInstallationId { get; set; }
    public long GrantRevision { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public AgentInstallation? RequesterInstallation { get; set; }
    public AgentInstallation? ProviderInstallation { get; set; }
}
