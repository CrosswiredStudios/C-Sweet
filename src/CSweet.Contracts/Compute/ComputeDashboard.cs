namespace CSweet.Contracts.Compute;

public sealed record ComputeDashboard(DateTimeOffset CheckedAt, ComputeSetupStatus? Setup,
    IReadOnlyList<ComputeResourceStatus> Resources, bool HasMore, bool CanManageNetwork = false);

public sealed record ComputeSetupStatus(string State, string Stage, DateTimeOffset? LastProgressAt,
    long? DownloadedBytes, string? FailureCode, long? TotalDownloadBytes = null,
    double? DownloadBytesPerSecond = null, double? DownloadSecondsRemaining = null, bool DownloadWaitingForProgress = false,
    int PreparationStep = 0, bool? InstallerRunning = null);

public sealed record ComputeResourceStatus(Guid Id, string Agent, string Project, string State,
    string DesiredState, string OperatingSystem, int CpuCount, long MemoryMiB, long DiskMiB,
    DateTimeOffset UpdatedAt, DateTimeOffset LeaseExpiresAt, string? FailureCode, bool LocalLinkAllowed = false);
