namespace CSweet.TrustedServices;

/// <summary>
/// One Git smart-HTTP exchange for an agent's assigned workspace. <c>Workspace.Operation</c> must be
/// <c>snapshot-pull</c> for <c>git-upload-pack</c> and <c>publish</c> for <c>git-receive-pack</c>, so the
/// exchange is authorized exactly like the snapshot transfer it replaces.
/// </summary>
public sealed record AgentBrokerWorkspaceGitRequest(AgentBrokerWorkspaceOperationRequest Workspace,
    string Service, bool Advertise, byte[] Body);
public sealed record AgentBrokerWorkspaceGitResult(string ContentType, byte[] Body);
