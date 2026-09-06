namespace CSweet.TrustedServices;

public sealed record GitHubInstallationRequest(long InstallationId);

public sealed record GitHubInstallationDescriptor(
    long InstallationId,
    long AccountId,
    string AccountLogin,
    string AccountType,
    bool Suspended,
    string? SuspendedReason);

public sealed record GitHubRepositoryDescriptor(
    long RepositoryId,
    string Owner,
    string Name,
    string FullName,
    string CloneUrl,
    string DefaultBranch,
    bool IsPrivate,
    bool IsArchived,
    bool IsTemplate);

public sealed record GitHubMergeRequest(
    long InstallationId,
    string Owner,
    string Repository,
    int PullRequestNumber,
    string ExpectedHeadSha,
    string IdempotencyKey);

public sealed record GitHubMergeResult(
    bool Merged,
    bool HeadMatched,
    string? MergeCommitSha,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record GitHubProvisionRepositoryRequest(
    long InstallationId,
    string OrganizationLogin,
    string RepositoryName,
    string Description,
    string TemplateOwner,
    string TemplateRepository,
    string RequiredDefaultBranch,
    string IdempotencyKey);

public sealed record GitHubProvisionRepositoryResult(
    bool Created,
    bool Quarantined,
    long? RepositoryId,
    string? Owner,
    string? Repository,
    string? DefaultBranch,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record AgentBrokerWorkspacePrepareRequest(
    Guid OrganizationId,
    Guid AgentInstallationId,
    Guid RepositoryId,
    Guid WorkspaceId,
    Guid WorkItemId,
    long AssignmentRevision,
    string DeterministicBranch,
    string? ExpectedCommitSha,
    string IdempotencyKey);

public sealed record AgentBrokerWorkspacePrepareResult(
    string WorkspaceKey,
    string AgentWorkspacePath,
    string BaseCommitSha,
    bool Resumed);

public sealed record GitHubWorkspacePrepareRequest(
    long InstallationId,
    long ExternalRepositoryId,
    string Owner,
    string Repository,
    string DefaultBranch,
    Guid WorkspaceId,
    string DeterministicBranch,
    string? ExpectedCommitSha,
    string IdempotencyKey);

public sealed record GitHubWorkspaceSnapshot(
    string WorkspaceKey,
    string BaseCommitSha,
    bool Resumed,
    byte[] Archive,
    WorkspaceArtifactManifest Manifest);

public static class WorkspaceSnapshotHeaders
{
    public const string WorkspaceKey = "X-CSweet-Workspace-Key";
    public const string BaseCommitSha = "X-CSweet-Base-Commit-Sha";
    public const string Resumed = "X-CSweet-Resumed";
    public const string ArtifactSha256 = "X-CSweet-Artifact-Sha256";
    public const string ArtifactFileCount = "X-CSweet-Artifact-File-Count";
    public const string ArtifactTotalBytes = "X-CSweet-Artifact-Total-Bytes";
}
public sealed record AgentBrokerWorkspaceOperationRequest(Guid OrganizationId, Guid RepositoryId, Guid WorkspaceId,
    Guid WorkItemId, long AssignmentRevision, string WorkspaceKey, string IdempotencyKey, string Operation,
    string? CommitMessage = null, bool RetainOnFailure = true, string? ProposedChangeTitle = null, string? ProposedChangeBody = null);
public sealed record AgentBrokerWorkspaceOperationResult(string Status, string BaseSha,
    IReadOnlyList<string> ChangedFiles, string DiffSummary, string? CommitSha = null, string? Branch = null,
    string? ReviewUrl = null, bool Removed = false, DateTimeOffset? RetainUntil = null, string Provider = "InternalGit");
