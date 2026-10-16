namespace CSweet.Contracts.Compute;

public sealed record ComputeDashboard(DateTimeOffset CheckedAt, ComputeSetupStatus? Setup,
    IReadOnlyList<ComputeResourceStatus> Resources, bool HasMore, bool CanManageNetwork = false);

public sealed record ComputeSetupStatus(string State, string Stage, DateTimeOffset? LastProgressAt,
    long? DownloadedBytes, string? FailureCode, long? TotalDownloadBytes = null,
    double? DownloadBytesPerSecond = null, double? DownloadSecondsRemaining = null, bool DownloadWaitingForProgress = false,
    int PreparationStep = 0, bool? InstallerRunning = null)
{
    public Guid? AttemptId { get; init; }
    public string? Phase { get; init; }
    public int Milestone { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? PhaseStartedAt { get; init; }
    public DateTimeOffset? InstallerCheckedAt { get; init; }
    public string Health { get; init; } = "Unknown";
    public bool CanRetry { get; init; }
    public IReadOnlyList<ComputePreparationEvent> Activity { get; init; } = [];
}

public sealed record ComputePreparationEvent(string Phase, string Message, DateTimeOffset At);
public sealed record ComputeCreationOption(Guid InstallationId, Guid ProjectId, string Agent, string Project);
public sealed record ComputeCreationOptions(IReadOnlyList<ComputeCreationOption> Options, int MaximumLifetimeSeconds);
public sealed record CreateLinuxInstance(Guid InstallationId, Guid ProjectId, string Name, Guid RequestId, int LifetimeSeconds);

public sealed record ComputeResourceStatus(Guid Id, string Agent, string Project, string State,
    string DesiredState, string OperatingSystem, int CpuCount, long MemoryMiB, long DiskMiB,
    DateTimeOffset UpdatedAt, DateTimeOffset LeaseExpiresAt, string? FailureCode, bool LocalLinkAllowed = false)
{
    public Guid? EmployeeId { get; init; }
    public bool LocalLinkRequested { get; init; }
    public string? Name { get; init; }
}
