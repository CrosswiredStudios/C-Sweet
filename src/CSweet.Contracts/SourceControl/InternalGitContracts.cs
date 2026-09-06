namespace CSweet.Contracts.SourceControl;

public sealed record InternalGitStorageStatus(bool Ready, string RepositoryRoot, string TemporaryRoot,
    string LfsProvider, string LfsLocation, string BackupProvider, string BackupLocation, string? Error);
public sealed record InternalGitRepositoryRequest(Guid OrganizationId, Guid RepositoryId,
    string Operation, string? Name = null, string? Ref = null, string? ExpectedSha = null,
    string? TargetSha = null, string? Path = null);
public sealed record InternalGitRef(string Name, string Sha);
public sealed record InternalGitCommit(string Sha, string Author, string Subject);
public sealed record InternalGitRepositoryInspection(string DefaultBranch,
    IReadOnlyList<InternalGitRef> Refs, IReadOnlyList<InternalGitCommit> Commits,
    IReadOnlyList<string> Files, string? Content = null);
public sealed record CreateInternalRepositoryRequest(string Name, string DefaultBranch = "main", Guid? TeamId = null);
public sealed record UpdateInternalRepositoryRequest(string Name, string DefaultBranch, bool Archived, long ExpectedRevision);
public sealed record InternalRepositoryDetails(SourceControlRepositorySummary Repository, long Revision,
    InternalGitRepositoryInspection Inspection);
public sealed record InternalGitWorkspaceRequest(Guid OrganizationId, Guid RepositoryId, Guid WorkspaceId,
    string DefaultBranch, string Branch, string? ExpectedSha, string IdempotencyKey);
public sealed record DeleteInternalRepositoryRequest(string ConfirmName, long ExpectedRevision);
public sealed record InternalGitRefRequest(string Operation, string Ref, string ExpectedSha, string? TargetSha = null);
public sealed record InternalGitSnapshotOperation(Guid OrganizationId, Guid RepositoryId, Guid WorkspaceId,
    string Operation, string BaseSha, string Branch, string DefaultBranch, string IdempotencyKey,
    byte[] Archive, string ArchiveManifestSha, int FileCount, long TotalBytes, string? CommitMessage = null, bool AllowLfs = true);
public sealed record InternalGitSnapshotResult(string Status, string BaseSha, string? CommitSha,
    IReadOnlyList<string> ChangedFiles, string DiffSummary, string? LatestTargetSha = null);
public sealed record InternalGitMergeRequest(Guid OrganizationId, Guid RepositoryId, Guid PublicationId,
    string SourceBranch, string TargetBranch, string ExpectedHeadSha, string IdempotencyKey);
public sealed record InternalGitMergeResult(bool Merged, bool HeadMatched, string? MergeCommitSha,
    string? FailureCode = null, string? FailureMessage = null);
public sealed record InternalGitProposalSummary(Guid Id, Guid RepositoryId, string CommitSha, string Branch,
    string TargetBranch, string Status, DateTimeOffset CreatedAt, bool QaPassed);
public sealed record SetInternalRepositoryTeamRequest(Guid TeamId, bool IsPrimary, string MergeApprovalMode, long ExpectedRevision = 0, bool Disabled = false);
public sealed record InternalGitTeamAccess(Guid TeamId, string TeamName, bool IsPrimary, string MergeApprovalMode, long Revision, bool Disabled);
public sealed record InternalGitProvisioningSettings(Guid TemplateId, bool Enabled, bool RequiresApproval, int MaximumRepositories,
    Guid? DefaultTeamId, string NamePrefix, string DefaultBranch, long Revision, IReadOnlyList<InternalGitProvisioningJob> Jobs);
public sealed record InternalGitProvisioningJob(Guid Id, string Name, string Status, Guid? RepositoryId, string? Message);
public sealed record UpdateInternalGitProvisioningSettings(bool Enabled, bool RequiresApproval, int MaximumRepositories,
    Guid? DefaultTeamId, string NamePrefix, string DefaultBranch, long ExpectedRevision);
public sealed record InternalGitHttpRequest(Guid OrganizationId, Guid RepositoryId, string Service, bool Advertise,
    byte[] Body, IReadOnlyList<string> ProtectedBranches, Guid? ActorId = null);
public sealed record InternalGitHttpResponse(string ContentType, byte[] Body);
public sealed record CreateInternalGitAccessRequest(string Name, bool CanPush = false, bool AllowDefaultBranchWrites = false, int LifetimeDays = 30);
public sealed record InternalGitAccessSummary(Guid Id, string Name, bool CanPush, bool AllowDefaultBranchWrites, DateTimeOffset ExpiresAt, bool Revoked);
public sealed record CreatedInternalGitAccess(InternalGitAccessSummary Credential, string Username, string Token);
public sealed record InternalGitAccessList(string CloneUrl, IReadOnlyList<InternalGitAccessSummary> Credentials);
public sealed record InternalGitLfsTransfer(Guid OrganizationId, Guid RepositoryId, string Operation, string Oid, long Size, byte[] Body);
public sealed record InternalGitLfsTransferResult(byte[] Body);
public sealed record InternalGitLfsBatch(string Operation, IReadOnlyList<InternalGitLfsObject> Objects, IReadOnlyList<string>? Transfers = null, [property: System.Text.Json.Serialization.JsonPropertyName("hash_algo")] string? HashAlgo = null);
public sealed record InternalGitLfsObject(string Oid, long Size);
public sealed record InternalGitBackupRequest(Guid OrganizationId, Guid RepositoryId, Guid BackupId);
public sealed record InternalGitBackupRestoreRequest(Guid OrganizationId, Guid RepositoryId, Guid BackupId, Guid TargetRepositoryId);
public sealed record InternalGitBackupSummary(Guid Id, Guid RepositoryId, DateTimeOffset CreatedAt, string DefaultBranch,
    long ArchiveBytes, string ArchiveSha256, int RefCount, int LfsObjectCount);
public sealed record CreateInternalGitBackupRequest(Guid BackupId);
public sealed record RestoreInternalGitBackupRequest(Guid RestoreId, string Name);

public sealed record InternalGitFileLock(string Id, string Path, Guid OwnerId, string OwnerName, DateTimeOffset LockedAt);
public sealed record InternalGitLockRequest(Guid OrganizationId, Guid RepositoryId, Guid ActorId, string ActorName,
    string Operation, string? Path = null, string? Id = null, bool Force = false, bool CanForce = false, string? Cursor = null, int Limit = 100);
public sealed record InternalGitLockResult(int StatusCode, IReadOnlyList<InternalGitFileLock> Locks, string? NextCursor = null, string? Message = null);
public sealed record ManageInternalGitLockRequest(string Operation, string? Path = null, string? Id = null, bool Force = false, string? Cursor = null);

public sealed record BusinessSourceControlDefaults(Guid? DefaultTemplateId, long Revision, IReadOnlyList<BusinessSourceControlDefaultOption> Options);
public sealed record BusinessSourceControlDefaultOption(Guid? TemplateId, string Provider, string ConnectionName, string TemplateName, bool Available, string? UnavailableReason);
public sealed record UpdateBusinessSourceControlDefaults(Guid? DefaultTemplateId, long ExpectedRevision);

public sealed record GitHubSnapshotOperation(long InstallationId, long ExternalRepositoryId, string Owner, string Repository,
    InternalGitSnapshotOperation Workspace, string? ProposedChangeTitle = null, string? ProposedChangeBody = null);
public sealed record GitHubSnapshotResult(InternalGitSnapshotResult Snapshot, string? PullRequestUrl = null);
