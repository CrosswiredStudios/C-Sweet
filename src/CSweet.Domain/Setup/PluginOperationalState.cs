namespace CSweet.Domain.Setup;

public sealed class PluginOperationalState
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ExternalKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
