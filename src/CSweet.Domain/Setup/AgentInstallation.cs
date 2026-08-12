namespace CSweet.Domain.Setup;

public sealed class AgentInstallation
{
    public Guid Id { get; set; }
    public Guid InstallationKey { get; set; }
    public int RevisionNumber { get; set; } = 1;
    public PluginRevisionStatus RevisionStatus { get; set; } = PluginRevisionStatus.Active;
    public Guid? SupersedesInstallationId { get; set; }
    public Guid? AgentDefinitionId { get; set; }
    public Guid PackageVersionId { get; set; }
    public Guid? ExecutionPoolId { get; set; }
    public string BusinessId { get; set; } = "default";
    public PluginInstallationScope Scope { get; set; } = PluginInstallationScope.Organization;
    public bool IsEnabled { get; set; } = true;
    public PluginSetupState SetupState { get; set; } = PluginSetupState.Ready;
    public string? SetupFlowId { get; set; }
    public string? SetupStepId { get; set; }
    public string SetupDataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long DesiredConfigurationRevision { get; set; }
    public long AppliedConfigurationRevision { get; set; }
    public AgentConfigurationSyncStatus ConfigurationSyncStatus { get; set; } = AgentConfigurationSyncStatus.Current;
    public DateTimeOffset? ConfigurationSyncLastAttemptAt { get; set; }
    public string? ConfigurationSyncLastError { get; set; }

    public AgentDefinition? AgentDefinition { get; set; }
    public AgentPackageVersion? PackageVersion { get; set; }
    public AgentInstallationGrant? Grant { get; set; }
    public AgentInstallationConfiguration? Configuration { get; set; }
    public AgentSchedule? Schedule { get; set; }
    public ICollection<AgentRuntimeInstance> RuntimeInstances { get; set; } = [];
    public ICollection<PluginConnection> Connections { get; set; } = [];
}

public enum AgentConfigurationSyncStatus
{
    Current,
    Refreshing,
    Restarting,
    Failed,
    PendingNextStart
}

public enum PluginRevisionStatus
{
    Staged,
    Active,
    Retired
}

public enum PluginSetupState
{
    NeedsSetup,
    Ready,
    ConnectionRequired,
    SetupFailed
}
