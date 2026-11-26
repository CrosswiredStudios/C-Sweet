namespace CSweet.Contracts.Compute;

public sealed record ComputeDashboard(DateTimeOffset CheckedAt, ComputeSetupStatus? Setup,
    IReadOnlyList<ComputeResourceStatus> Resources, bool HasMore, bool CanManageNetwork = false,
    bool LegacyLocalSetupEnabled = false)
{
    public IReadOnlyList<ComputeOfficeStatus> Offices { get; init; } = [];
    public IReadOnlyList<ComputeRequestStatus> Requests { get; init; } = [];
}

public sealed record ComputeWorkspaceCapacity(int Slots, int CpuCount, long MemoryMiB, long DiskMiB)
{
    public int StandardWorkspaces => Math.Max(0,
        (int)Math.Min(Math.Min(Slots, CpuCount / 2), Math.Min(MemoryMiB / 2048, DiskMiB / 20480)));
}

public sealed record ComputeOfficeStatus(Guid Id, string Name, string State, DateTimeOffset? LastHeartbeatAt,
    string? TemplateId, IReadOnlyList<string> Features, ComputeWorkspaceCapacity? Total, ComputeWorkspaceCapacity? Free,
    bool MayReassign = false);

public sealed record ComputeRequestStatus(Guid Id, Guid ProjectId, Guid EmployeeId, string Employee, string Project,
    bool RequiresBrowser, bool CanRecheck, DateTimeOffset RequestedAt, DateTimeOffset LastCheckedAt, DateTimeOffset? RetryAt, string Reason,
    string? DiagnosticCode, IReadOnlyList<ComputeRequestWork> Work);

public sealed record ComputeRequestWork(Guid ItemId, string Title, string? Identifier, string Uri);

public sealed record ComputeOperationStatus(Guid Id, string Action, string State, int Attempts, DateTimeOffset UpdatedAt, string? FailureCode,
    bool CanRecheck = false);

public sealed record ComputeActionResult(string Message);

public sealed record ComputeSetupStatus(string State, string Stage, DateTimeOffset? LastProgressAt,
    long? DownloadedBytes, string? FailureCode, long? TotalDownloadBytes = null,
    double? DownloadBytesPerSecond = null, double? DownloadSecondsRemaining = null, bool DownloadWaitingForProgress = false,
    int PreparationStep = 0, bool? InstallerRunning = null)
{
    public const string AwaitingOfficeRegistration = "AwaitingOfficeRegistration";
    public OfficeComputeCapacityStatus? OfficeCapacity { get; init; }
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

public sealed record OfficeComputeCapacityStatus(bool HasRegisteredOffice, int WaitingEmployees,
    string? FirstWaitingEmployee, DateTimeOffset? FirstRequestedAt);

public sealed record ComputePreparationEvent(string Phase, string Message, DateTimeOffset At);
public sealed record ComputeCreationOption(Guid InstallationId, Guid ProjectId, string Agent, string Project);
public sealed record ComputeCreationOptions(IReadOnlyList<ComputeCreationOption> Options, int MaximumLifetimeSeconds);
public sealed record CreateLinuxInstance(Guid InstallationId, Guid ProjectId, string Name, Guid RequestId, int LifetimeSeconds);

public sealed record ComputeResourceStatus(Guid Id, string Agent, string Project, string State,
    string DesiredState, string OperatingSystem, int CpuCount, long MemoryMiB, long DiskMiB,
    DateTimeOffset UpdatedAt, DateTimeOffset LeaseExpiresAt, string? FailureCode, bool LocalLinkAllowed = false)
{
    public long Generation { get; init; }
    public bool CanPowerOn { get; init; }
    public bool CanPowerOff { get; init; }
    public Guid? EmployeeId { get; init; }
    public bool LocalLinkRequested { get; init; }
    public string? Name { get; init; }
    public ComputeOperationStatus? LatestOperation { get; init; }
}
