namespace CSweet.AgentRuntime.Artifacts;

public sealed class ArtifactMediaOptions
{
    public const string SectionName = "CSweet:AgentRuntime:ArtifactMedia";
    public string RootPath { get; set; } = string.Empty;

    public string ValidatedRootPath()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || !Path.IsPathFullyQualified(RootPath))
            throw new InvalidOperationException("The artifact media root must be an absolute path.");
        var fullPath = Path.GetFullPath(RootPath);
        if (Path.GetPathRoot(fullPath) == fullPath)
            throw new InvalidOperationException("The artifact media root cannot be a filesystem root.");
        return fullPath;
    }
}
