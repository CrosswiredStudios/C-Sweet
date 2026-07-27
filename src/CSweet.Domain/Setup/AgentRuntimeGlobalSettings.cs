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
    public int GlobalMaxActiveContainers { get; set; } = 10;
    public int PerBusinessMaxActiveContainers { get; set; } = 5;
    public int PerInstallationMaxActiveContainers { get; set; } = 1;
    public int DefaultContainerMemoryMb { get; set; } = 512;
    public int MaximumContainerMemoryMb { get; set; } = 2048;
    public int DefaultContainerCpuPercent { get; set; } = 50;
    public int MaximumContainerCpuPercent { get; set; } = 200;
    public int DefaultContainerPidsLimit { get; set; } = 100;
    public int DefaultContainerLogLimitMb { get; set; } = 10;
    public int ContainerStartTimeoutSeconds { get; set; } = 60;
    public int McpSessionTimeoutSeconds { get; set; } = 30;
    public int ContainerStopGraceSeconds { get; set; } = 15;
    public string DefaultNetworkPolicy { get; set; } = "McpOnly";
    public bool AllowPublicInternetByDefault { get; set; }
    public string AllowedPackageFeedHosts { get; set; } = string.Empty;
    public string BlockedNetworkCidrs { get; set; } = string.Empty;
    public string AgentSourceRootPath { get; set; } = string.Empty;
    public string AgentPackageCachePath { get; set; } = string.Empty;
    public string DotNetBuilderImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public string DotNetRuntimeBaseImage { get; set; } = "mcr.microsoft.com/dotnet/runtime:10.0";
    public int BuildTimeoutSeconds { get; set; } = 600;
    public int BuildMemoryMb { get; set; } = 2048;
    public int BuildCpuPercent { get; set; } = 200;
    public int MaximumRepositorySizeMb { get; set; } = 500;
    public int MaximumBuildLogMb { get; set; } = 10;
    public bool KeepFailedBuildWorkspaces { get; set; }
    public int CompletedRuntimeRetentionDays { get; set; } = 14;
    public int FailedRuntimeRetentionDays { get; set; } = 30;
    public int BuildLogRetentionDays { get; set; } = 30;
    public bool RemoveContainersAfterCompletion { get; set; } = true;
    public bool RemoveWorkspacesAfterCompletion { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}
