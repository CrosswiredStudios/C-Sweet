namespace CSweet.Contracts.Setup;

public sealed record AgentIsolationOnboardingResponse(
    string ProviderId,
    string ProviderDisplayName,
    string HostOperatingSystem,
    string HostEdition,
    bool IsSupportedHost,
    bool IsHardwareVirtualizationAvailable,
    bool IsHypervisorFeatureEnabled,
    bool IsHypervisorRunning,
    bool IsRestartPending,
    bool IsRuntimeHostReachable,
    bool IsProviderCertified,
    bool IsReady,
    bool CanAutomateHypervisorEnablement,
    bool CanAutomateRuntimeHostInstallation,
    string RuntimeHostProvisioningMode,
    string RuntimeHostActionLabel,
    string RuntimeHostActionDescription,
    string Summary,
    string DocumentationUrl,
    AgentIsolationProvisioningProgressResponse? ProvisioningProgress,
    IReadOnlyList<AgentIsolationOnboardingCheckResponse> Checks);

public sealed record AgentIsolationProvisioningProgressResponse(
    Guid JobId,
    string Workflow,
    string State,
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
    string? ErrorMessage);

public sealed record AgentIsolationOnboardingCheckResponse(
    string Key,
    string DisplayName,
    string Status,
    string Message,
    string? Remediation = null);

public sealed record AgentIsolationOnboardingActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    bool ElevationPromptStarted,
    AgentIsolationOnboardingResponse Status);
