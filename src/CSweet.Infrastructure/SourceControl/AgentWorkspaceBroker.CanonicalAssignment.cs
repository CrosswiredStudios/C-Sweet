namespace CSweet.Infrastructure.SourceControl;
using CSweet.Agent.SDK;
public sealed partial class AgentWorkspaceBroker
{
    private static string WorkspaceAction(string operation) => operation switch
    {
        "inspect" => GitWorkspaceCapabilities.Inspect,
        "publish" => GitWorkspaceCapabilities.Publish,
        "refresh" => GitWorkspaceCapabilities.Refresh,
        "cleanup" => GitWorkspaceCapabilities.Cleanup,
        "snapshot-pull" => GitWorkspaceCapabilities.Prepare,
        "snapshot-push" => GitWorkspaceCapabilities.Publish,
        _ => throw new ArgumentException("Unsupported workspace operation.")
    };
}
