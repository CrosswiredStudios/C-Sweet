namespace CSweet.Domain.Setup;

public sealed class AgentInstallation
{
    public Guid Id { get; set; }
    public Guid InstallationKey { get; set; }
    public int RevisionNumber { get; set; } = 1;
    public PluginRevisionStatus RevisionStatus { get; set; } = PluginRevisionStatus.Active;
    public Guid? SupersedesInstallationId { get; set; }
    public Guid PackageVersionId { get; set; }
    public string BusinessId { get; set; } = "default";
    public PluginInstallationScope Scope { get; set; } = PluginInstallationScope.Organization;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AgentPackageVersion? PackageVersion { get; set; }
    public AgentInstallationGrant? Grant { get; set; }
    public AgentInstallationConfiguration? Configuration { get; set; }
    public AgentSchedule? Schedule { get; set; }
    public ICollection<AgentRuntimeInstance> RuntimeInstances { get; set; } = [];
}

public enum PluginRevisionStatus
{
    Staged,
    Active,
    Retired
}
