namespace CSweet.ExecutionNode;

public sealed class ExecutionNodeOptions
{
    public const string SectionName = "CSweet:ExecutionNode";

    public string ControlPlaneUrl { get; set; } = "https://localhost:7443";
    public string EnrollmentToken { get; set; } = string.Empty;
    public string EnrollmentTokenFilePath { get; set; } = string.Empty;
    public string DevelopmentBootstrapKey { get; set; } = string.Empty;
    public string StateDirectory { get; set; } = string.Empty;
    public string ArtifactCacheDirectory { get; set; } = string.Empty;
    public string ArtifactMediaDirectory { get; set; } = string.Empty;
    public string NodeName { get; set; } = Environment.MachineName;
    public int AllocatableCpuCount { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public int AllocatableMemoryMb { get; set; } = 4096;
    public int AllocatableDiskMb { get; set; } = 32768;
    public int MaximumConcurrentWorkloads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public string ResolveStateDirectory()
    {
        if (!string.IsNullOrWhiteSpace(StateDirectory)) return Path.GetFullPath(StateDirectory);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : local,
            "CSweet", "ExecutionNode");
    }

    public string ResolveArtifactCacheDirectory() => string.IsNullOrWhiteSpace(ArtifactCacheDirectory)
        ? Path.Combine(ResolveStateDirectory(), "artifact-cache")
        : Path.GetFullPath(ArtifactCacheDirectory);

    public string ResolveArtifactMediaDirectory() => string.IsNullOrWhiteSpace(ArtifactMediaDirectory)
        ? Path.Combine(ResolveStateDirectory(), "artifact-media")
        : Path.GetFullPath(ArtifactMediaDirectory);
}
