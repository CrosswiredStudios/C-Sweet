namespace CSweet.Infrastructure.SourceControl;

public sealed class WorkspaceSyncTransferOptions
{
    public const string SectionName = "CSweet:SourceControl:WorkspaceSync";

    public int MaximumArchiveBytes { get; set; }
    public long MaximumExpandedBytes { get; set; }
    public int MaximumFileCount { get; set; }

    /// <summary>Largest file an agent may push as an ordinary Git object. Larger files must use Git LFS.</summary>
    public long MaximumGitBlobBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>
    /// When true, a prepared team workspace on an internal repository is told its work branch, which makes
    /// agents on SDK 5.20.0 or later deliver with git instead of snapshots. Off by default.
    /// </summary>
    public bool AgentGitDelivery { get; set; }
}
