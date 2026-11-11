namespace CSweet.Infrastructure.SourceControl;

public sealed class WorkspaceSyncTransferOptions
{
    public const string SectionName = "CSweet:SourceControl:WorkspaceSync";

    public int MaximumArchiveBytes { get; set; }
    public long MaximumExpandedBytes { get; set; }
    public int MaximumFileCount { get; set; }

    /// <summary>Largest file an agent may push as an ordinary Git object. Larger files must use Git LFS.</summary>
    public long MaximumGitBlobBytes { get; set; } = 10L * 1024 * 1024;
}
