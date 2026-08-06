namespace CSweet.AgentRuntime.Artifacts;

public sealed class ArtifactStoreOptions
{
    public const string SectionName = "CSweet:AgentRuntime:Artifacts";

    public string RootPath { get; set; } = string.Empty;
    public int MaximumFileCount { get; set; } = 10_000;
    public int MaximumPathLength { get; set; } = 512;
    public long MaximumUncompressedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaximumManifestBytes { get; set; } = 1024 * 1024;

    public string ValidatedRootPath()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || !Path.IsPathFullyQualified(RootPath))
            throw new InvalidOperationException("The artifact store root must be an absolute path.");
        var fullPath = Path.GetFullPath(RootPath);
        if (Path.GetPathRoot(fullPath) == fullPath)
            throw new InvalidOperationException("The artifact store root cannot be a filesystem root.");
        if (MaximumFileCount is < 1 or > 1_000_000 ||
            MaximumPathLength is < 32 or > 4096 ||
            MaximumUncompressedBytes is < 1 or > 100L * 1024 * 1024 * 1024 ||
            MaximumManifestBytes is < 128 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("One or more artifact validation limits are invalid.");
        return fullPath;
    }
}
