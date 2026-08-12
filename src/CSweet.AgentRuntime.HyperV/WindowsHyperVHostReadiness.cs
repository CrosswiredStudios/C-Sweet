namespace CSweet.AgentRuntime.HyperV;

public enum WindowsOptionalFeatureState
{
    Unknown,
    Disabled,
    Enabled,
    EnablePending,
    DisablePending
}

public sealed record WindowsHyperVHostReadiness(
    bool IsWindows,
    string ProductName,
    string EditionId,
    bool IsSupportedEdition,
    bool HasSecondLevelAddressTranslation,
    bool IsVirtualizationEnabledInFirmware,
    bool IsDataExecutionPreventionEnabled,
    long PhysicalMemoryBytes,
    WindowsOptionalFeatureState FeatureState,
    bool IsHypervisorPresent,
    bool IsRestartPending,
    bool CanLaunchElevation,
    string? DiagnosticError)
{
    public bool HardwareRequirementsSatisfied =>
        IsHypervisorPresent ||
        HasSecondLevelAddressTranslation &&
        IsVirtualizationEnabledInFirmware &&
        IsDataExecutionPreventionEnabled &&
        PhysicalMemoryBytes >= 4L * 1024 * 1024 * 1024;
}

public interface IWindowsHyperVHostProbe
{
    Task<WindowsHyperVHostReadiness> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record WindowsHyperVEnablementResult(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    bool ElevationPromptStarted);

public interface IWindowsHyperVFeatureProvisioner
{
    Task<WindowsHyperVEnablementResult> LaunchEnablementAsync(CancellationToken cancellationToken = default);
}

public sealed record WindowsRuntimeHostInstallResult(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    bool ElevationPromptStarted);

public enum WindowsRuntimeHostProvisioningAction
{
    Prepare,
    RepairAccess
}

public enum WindowsRuntimeHostProvisioningMode
{
    Unavailable,
    PackagedInstaller,
    DeveloperBootstrap,
    AccessRepair
}

public sealed record WindowsRuntimeHostProvisioningInfo(
    WindowsRuntimeHostProvisioningMode Mode,
    bool CanLaunch,
    string ActionLabel,
    string Description,
    string? UnavailableReason = null);

public enum WindowsRuntimeHostProvisioningState
{
    Running,
    RestartRequired,
    Completed,
    Failed
}

public sealed record WindowsRuntimeHostProvisioningProgress(
    Guid JobId,
    string Workflow,
    WindowsRuntimeHostProvisioningState State,
    string PhaseKey,
    string PhaseDisplayName,
    string Message,
    int PercentComplete,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int? EstimatedRemainingMinimumSeconds,
    int? EstimatedRemainingMaximumSeconds,
    bool RequiresRestart,
    string? ErrorCode,
    string? ErrorMessage,
    int? OwnerProcessId);

public interface IWindowsRuntimeHostProvisioner
{
    WindowsRuntimeHostProvisioningInfo GetProvisioningInfo(bool preferAccessRepair = false);
    WindowsRuntimeHostProvisioningProgress? GetProgress();
    Task<WindowsRuntimeHostInstallResult> LaunchInstallerAsync(
        WindowsRuntimeHostProvisioningAction action,
        CancellationToken cancellationToken = default);
    Task<WindowsRuntimeHostInstallResult> LaunchInstallerAsync(
        WindowsRuntimeHostProvisioningAction action,
        string controlPlaneUrl,
        string enrollmentToken,
        CancellationToken cancellationToken = default) =>
        LaunchInstallerAsync(action, cancellationToken);
}
