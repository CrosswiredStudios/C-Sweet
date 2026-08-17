# Agent Runtime Manager - Global Configuration

## Goal

Add an Agent Runtime section to the Configuration page. This section controls global defaults and
hard limits for imported agents, builds, schedules, and containers.

Do not hide these settings only in `appsettings.json`. Self-hosted users need to inspect and tune
them from the UI.

## Configuration page section

Add a new section below LLM provider management:

```text
Agent Runtime
  Runtime mode defaults
  Container limits
  Scheduling limits
  Build/import limits
  Cleanup and retention
```

Use normal form controls:

- Numeric fields for limits.
- Toggles for enable/disable settings.
- Selects for modes.
- Text fields for image names and paths.

## Suggested settings

### Runtime defaults

- `EnableImportedAgents`
- `DefaultActivationMode`: `OnDemand`
- `DefaultTickFrequencySeconds`
- `MinimumTickFrequencySeconds`
- `DefaultMaxRuntimeSeconds`
- `DefaultOverlapPolicy`: `Skip`
- `AllowAlwaysOnCommunityAgents`
- `DefaultRestartPolicy`: `Never`, `OnFailure`, `Always`

### Container limits

- `GlobalMaxActiveContainers`
- `PerBusinessMaxActiveContainers`
- `PerInstallationMaxActiveContainers`
- `DefaultContainerMemoryMb`
- `MaximumContainerMemoryMb`
- `DefaultContainerCpuPercent`
- `MaximumContainerCpuPercent`
- `DefaultContainerPidsLimit`
- `DefaultContainerLogLimitMb`
- `ContainerStartTimeoutSeconds`
- `BrokerRegistrationTimeoutSeconds`
- `ContainerStopGraceSeconds`

### Network limits

- `DefaultNetworkPolicy`: `BrokerOnly`
- `AllowPublicInternetByDefault`
- `AllowedPackageFeedHosts`
- `BlockedNetworkCidrs`

For first implementation, persist these settings even if network enforcement starts simple. The UI
contract should be ready for stricter enforcement.

### Import/build settings

- `AgentSourceRootPath`
- `AgentPackageCachePath`
- `DotNetBuilderImage`
- `DotNetRuntimeBaseImage`
- `BuildTimeoutSeconds`
- `BuildMemoryMb`
- `BuildCpuPercent`
- `MaximumRepositorySizeMb`
- `MaximumBuildLogMb`
- `KeepFailedBuildWorkspaces`

Defaults should point to application-owned data directories, not the repository working tree.

### Cleanup settings

- `CompletedRuntimeRetentionDays`
- `FailedRuntimeRetentionDays`
- `BuildLogRetentionDays`
- `RemoveContainersAfterCompletion`
- `RemoveWorkspacesAfterCompletion`

## Storage

Prefer database-backed settings so the UI can edit them.

Suggested entity:

```csharp
public sealed class AgentRuntimeGlobalSettings
{
    public Guid Id { get; set; }
    public bool EnableImportedAgents { get; set; }
    public string DefaultActivationMode { get; set; } = "OnDemand";
    public int DefaultTickFrequencySeconds { get; set; } = 3600;
    public int MinimumTickFrequencySeconds { get; set; } = 300;
    public int DefaultMaxRuntimeSeconds { get; set; } = 600;
    public int GlobalMaxActiveContainers { get; set; } = 10;
    public int PerBusinessMaxActiveContainers { get; set; } = 5;
    public int PerInstallationMaxActiveContainers { get; set; } = 1;
    public int DefaultContainerMemoryMb { get; set; } = 512;
    public int MaximumContainerMemoryMb { get; set; } = 2048;
    public int DefaultContainerCpuPercent { get; set; } = 50;
    public int MaximumContainerCpuPercent { get; set; } = 200;
    public int ContainerStartTimeoutSeconds { get; set; } = 60;
    public int BrokerRegistrationTimeoutSeconds { get; set; } = 30;
    public int ContainerStopGraceSeconds { get; set; } = 15;
    public string DotNetBuilderImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public string DotNetRuntimeBaseImage { get; set; } = "mcr.microsoft.com/dotnet/runtime:10.0";
    public int BuildTimeoutSeconds { get; set; } = 600;
    public int BuildMemoryMb { get; set; } = 2048;
    public int BuildCpuPercent { get; set; } = 200;
    public int CompletedRuntimeRetentionDays { get; set; } = 14;
    public int FailedRuntimeRetentionDays { get; set; } = 30;
    public bool RemoveContainersAfterCompletion { get; set; } = true;
    public bool RemoveWorkspacesAfterCompletion { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}
```

The exact fields can be adjusted during implementation, but keep the defaults conservative.

## API shape

Add endpoints under:

```text
GET /api/agent-runtime/settings
PUT /api/agent-runtime/settings
```

Request/response DTOs should live in `CSweet.Contracts`.

Validation rules:

- Minimum tick frequency must be at least 60 seconds.
- Default tick frequency must be greater than or equal to minimum tick frequency.
- Default max runtime must be less than or equal to a platform hard maximum.
- Default memory must be less than or equal to maximum memory.
- Default CPU must be less than or equal to maximum CPU.
- Container limits must be positive integers.
- Builder/runtime image names cannot be empty.
- Paths cannot point inside the application source tree by default.

## UI acceptance criteria

- Configuration page shows current Agent Runtime settings.
- Admin can save changes.
- Invalid settings show field-level or form-level errors.
- Saving creates an audit event.
- Settings affect newly created imports/schedules.
- Settings do not silently mutate existing installation grants unless the value is a hard global
  maximum.
