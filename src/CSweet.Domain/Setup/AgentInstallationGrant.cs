namespace CSweet.Domain.Setup;

public sealed class AgentInstallationGrant
{
    public Guid Id { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string NetworkAccessJson { get; set; } = "[]";
    public string ProvidedCapabilitiesJson { get; set; } = "[]";
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public string EventSubscriptionsJson { get; set; } = "[]";
    public string ResourceLimitsJson { get; set; } = "{}";
    public long GrantRevision { get; set; } = 1;
    public int MaxRuntimeSeconds { get; set; }
    public int MemoryMb { get; set; }
    public int CpuPercent { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }

    public AgentInstallation? AgentInstallation { get; set; }
}
