namespace CSweet.Infrastructure.SourceControl;

public sealed class WorkspaceSyncTransferOptions
{
    public const string SectionName = "CSweet:SourceControl:WorkspaceSync";

    public int MaximumArchiveBytes { get; set; }
    public long MaximumExpandedBytes { get; set; }
    public int MaximumFileCount { get; set; }
}
