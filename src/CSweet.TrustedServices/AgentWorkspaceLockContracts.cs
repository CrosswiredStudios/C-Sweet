namespace CSweet.TrustedServices;

public sealed record AgentBrokerWorkspaceLockRequest(AgentBrokerWorkspaceOperationRequest Workspace,
    string Operation, string? Path = null, string? Id = null, string? Cursor = null);
public sealed record AgentBrokerWorkspaceFileLock(string Id, string Path, string OwnerName, bool OwnedByCaller, DateTimeOffset LockedAt);
public sealed record AgentBrokerWorkspaceLockResult(string Status, IReadOnlyList<AgentBrokerWorkspaceFileLock> Locks, string? NextCursor = null, string? Message = null);
