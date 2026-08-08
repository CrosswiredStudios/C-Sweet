namespace CSweet.Domain.Setup;

public sealed class AgentRuntimeGlobalSettings
{
    public Guid Id { get; set; }
    public bool EnableImportedAgents { get; set; }
    public ActivationMode DefaultActivationMode { get; set; }
    public int DefaultTickFrequencySeconds { get; set; } = 3600;
    public int MinimumTickFrequencySeconds { get; set; } = 300;
    public int DefaultMaxRuntimeSeconds { get; set; } = 600;
    public OverlapPolicy DefaultOverlapPolicy { get; set; }
    public bool AllowAlwaysOnCommunityAgents { get; set; }
    public RestartPolicy DefaultRestartPolicy { get; set; }
    public int GlobalMaxActiveWorkloads { get; set; } = 10;
    public int PerBusinessMaxActiveWorkloads { get; set; } = 5;
    public int PerInstallationMaxActiveWorkloads { get; set; } = 1;
    public int DefaultWorkloadMemoryMb { get; set; } = 1024;
    public int MaximumWorkloadMemoryMb { get; set; } = 2048;
    public int DefaultWorkloadCpuPercent { get; set; } = 50;
    public int MaximumWorkloadCpuPercent { get; set; } = 200;
    public int DefaultWorkloadProcessLimit { get; set; } = 100;
    public int DefaultWorkloadLogLimitMb { get; set; } = 10;
    public int WorkloadStartTimeoutSeconds { get; set; } = 60;
    public int McpSessionTimeoutSeconds { get; set; } = 30;
    public int WorkloadStopGraceSeconds { get; set; } = 15;
    public string DefaultNetworkPolicy { get; set; } = "McpOnly";
    public bool AllowPublicInternetByDefault { get; set; }
    public string AllowedPackageFeedHosts { get; set; } = string.Empty;
    public string BlockedNetworkCidrs { get; set; } = string.Empty;
    public int BuildTimeoutSeconds { get; set; } = 600;
    public int BuildMemoryMb { get; set; } = 2048;
    public int BuildCpuPercent { get; set; } = 200;
    public int MaximumRepositorySizeMb { get; set; } = 500;
    public int MaximumBuildLogMb { get; set; } = 10;
    public bool KeepFailedBuildWorkspaces { get; set; }
    public int CompletedRuntimeRetentionDays { get; set; } = 14;
    public int FailedRuntimeRetentionDays { get; set; } = 30;
    public int BuildLogRetentionDays { get; set; } = 30;
    public bool RemoveWorkloadsAfterCompletion { get; set; } = true;
    public bool RemoveWorkspacesAfterCompletion { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}
