namespace CSweet.Domain.Setup;

public enum GitRepositoryProvider
{
    GitHub,
    GenericGit
}

public enum GitAuthenticationMode
{
    Anonymous,
    GitHubApp,
    HttpsCredential,
    Ssh
}

[Flags]
public enum GitAllowedOperation
{
    ReadFetch = 1,
    PushTicketBranch = 2,
    MergeQaApprovedPullRequest = 4
}

public enum GitPullRequestProvider
{
    None,
    GitHub
}

/// <summary>
/// Organization-owned repository allowlist entry. Secret values are held separately
/// in the installation-scoped encrypted plugin secret store.
/// </summary>
public sealed class GitRepositoryConnection
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public GitRepositoryProvider Provider { get; set; }
    public string CloneUrl { get; set; } = string.Empty;
    public string PermittedRepositoryPath { get; set; } = string.Empty;
    public GitAuthenticationMode AuthenticationMode { get; set; }
    public GitAllowedOperation AllowedOperations { get; set; } = GitAllowedOperation.ReadFetch;
    public string DefaultBranch { get; set; } = "main";
    public GitPullRequestProvider PullRequestProvider { get; set; }
    public string AllowedHostsJson { get; set; } = "[]";
    public string AllowedPortsJson { get; set; } = "[]";
    public string SshHostFingerprintsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<GitRepositoryConnectionGrant> InstallationGrants { get; set; } = [];
}

public sealed class GitRepositoryConnectionGrant
{
    public Guid Id { get; set; }
    public Guid RepositoryConnectionId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public bool CanReadFetch { get; set; } = true;
    public bool CanPushTicketBranch { get; set; }
    public bool CanMergeQaApprovedPullRequest { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public GitRepositoryConnection? RepositoryConnection { get; set; }
    public AgentInstallation? AgentInstallation { get; set; }
}

public enum GitTicketWorkspaceStatus
{
    Preparing,
    Ready,
    Published,
    Failed,
    Removed
}

public sealed class GitTicketWorkspace
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public Guid WorkItemId { get; set; }
    public long AssignmentRevision { get; set; }
    public Guid RepositoryConnectionId { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = "main";
    public string BranchName { get; set; } = string.Empty;
    public GitTicketWorkspaceStatus Status { get; set; }
    public string? CommitSha { get; set; }
    public string? PullRequestUrl { get; set; }
    public string ChangedFilesJson { get; set; } = "[]";
    public string ValidationsJson { get; set; } = "[]";
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetainUntil { get; set; }
    public string MergeStatus { get; set; } = "None";
    public string? MergeCommitSha { get; set; }
    public DateTimeOffset? MergedAt { get; set; }

    public GitRepositoryConnection? RepositoryConnection { get; set; }
    public AgentInstallation? AgentInstallation { get; set; }
}
