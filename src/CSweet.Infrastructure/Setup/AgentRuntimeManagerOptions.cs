namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeManagerOptions
{
    public const string SectionName = "CSweet:AgentRuntime";
    public const string CurrentDevelopmentCertificationSuiteVersion = "csweet-windows-hyperv-smoke-v13";
    public string RequiredCertificationSuiteVersion { get; set; } = CurrentDevelopmentCertificationSuiteVersion;
    public bool CleanupWorkloadsOnStartup { get; set; } = true;
    public string WorkspaceSnapshotStorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CSweet",
        "workspace-snapshots");
    public string SourceArchiveStorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CSweet",
        "agent-source-archives");
    public string BuildLogStorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CSweet",
        "agent-build-logs");
    public int MaximumScheduleClaimsPerIteration { get; set; } = 10;
    public int InteractiveIdleTimeoutSeconds { get; set; } = 300;
    public string? PreferredIsolationProviderId { get; set; }
    public string RuntimeGuestImageId { get; set; } = "csweet-runtime-base";
    public string RuntimeGuestImageVersion { get; set; } = string.Empty;
    public string RuntimeGuestImageDigest { get; set; } = string.Empty;
    public string RuntimeGuestOperatingSystem { get; set; } = "linux";
    public string RuntimeGuestArchitecture { get; set; } = "x64";
    public int RuntimeWritableDiskMb { get; set; } = 1024;
    public string BuilderGuestImageId { get; set; } = "csweet-builder-base";
    public string BuilderGuestImageVersion { get; set; } = string.Empty;
    public string BuilderGuestImageDigest { get; set; } = string.Empty;
    public string BuilderGuestOperatingSystem { get; set; } = "linux";
    public string BuilderGuestArchitecture { get; set; } = "x64";
}
