namespace CSweet.Domain.Setup;

public sealed class AgentInstallationConfiguration
{
    public Guid Id { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AgentInstallation? AgentInstallation { get; set; }
}
