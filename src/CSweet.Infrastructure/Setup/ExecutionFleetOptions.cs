namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionFleetOptions
{
    public const string SectionName = "CSweet:ExecutionFleet";
    public bool PublicLaunchEnabled { get; set; }
    public string ReleaseManifestUrl { get; set; } =
        "https://github.com/CrosswiredStudios/CSweet.Office/releases/latest/download/office-release.json";
    public string? WindowsPackageOverrideUrl { get; set; }
    public string? LinuxPackageOverrideUrl { get; set; }
    public string? MacOsPackageOverrideUrl { get; set; }
    public bool AllowUnpinnedDevelopmentImages { get; set; }
    public int MinimumBuilderCpuCount { get; set; } = 1;
    public int MinimumBuilderMemoryMb { get; set; } = 4096;
    public int MinimumBuilderDiskMb { get; set; } = 3072;
    public int MinimumRuntimeCpuCount { get; set; } = 1;
    public int MinimumRuntimeMemoryMb { get; set; } = 512;
    public int MinimumRuntimeDiskMb { get; set; } = 1024;
}
