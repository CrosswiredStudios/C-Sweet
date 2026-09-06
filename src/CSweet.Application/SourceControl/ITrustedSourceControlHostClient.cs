using CSweet.Contracts.SourceControl;
namespace CSweet.Application.SourceControl;

/// <summary>
/// Core-to-host boundary for provider mutations. Implementations resolve credentials from their
/// own trusted store; callers supply only authoritative Core identifiers and exact revisions.
/// </summary>
public interface ITrustedSourceControlHostClient
{
    Task<GitHubSnapshotResult> ApplyGitHubSnapshotAsync(GitHubSnapshotOperation request, CancellationToken ct = default) => throw new InvalidOperationException("GitHub workspace operations are unavailable.");
    Task<InternalGitLockResult> InternalLocksAsync(InternalGitLockRequest request, CancellationToken ct = default) => throw new InvalidOperationException("LFS locking is unavailable.");
    Task<IReadOnlyList<InternalGitBackupSummary>> ListInternalBackupsAsync(Guid business, CancellationToken ct = default) => throw new InvalidOperationException("Backup inspection is unavailable.");
    Task<InternalGitBackupSummary> CreateInternalBackupAsync(InternalGitBackupRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Backup creation is unavailable.");
    Task<InternalGitBackupSummary> RestoreInternalBackupAsync(InternalGitBackupRestoreRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Backup restore is unavailable.");
    Task DeleteInternalBackupAsync(InternalGitBackupRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Backup deletion is unavailable.");
    Task<InternalGitLfsTransferResult> TransferInternalLfsAsync(InternalGitLfsTransfer request, CancellationToken ct = default) => throw new InvalidOperationException("LFS client transfer is unavailable.");
    Task<InternalGitHttpResponse> ExchangeInternalGitAsync(InternalGitHttpRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Git client transport is unavailable.");
    Task<InternalGitSnapshotResult> ApplyInternalSnapshotAsync(InternalGitSnapshotOperation request,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("Internal publication is unavailable.");
    Task<InternalGitMergeResult> MergeInternalAsync(InternalGitMergeRequest request,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("Internal merge is unavailable.");

    Task<TrustedWorkspaceSnapshot> PrepareInternalWorkspaceAsync(InternalGitWorkspaceRequest request,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("Internal GitHost is unavailable.");

    Task<InternalGitStorageStatus> GetInternalStorageStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InternalGitStorageStatus(false, "", "", "filesystem", "", "filesystem", "", "Internal GitHost is unavailable."));

    Task<InternalGitRepositoryInspection> ExecuteInternalAsync(InternalGitRepositoryRequest request,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("Internal GitHost is unavailable.");

    Task<TrustedGitHubAppConfigurationStatus> GetConfigurationStatusAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(
        new TrustedGitHubAppConfigurationStatus(false, null, 0, null, null, "Configuration management is unavailable."));

    Task<TrustedGitHubAppConfigurationStatus> ValidateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(
        "This trusted host does not support runtime configuration validation.");

    Task<TrustedGitHubAppConfigurationStatus> ActivateConfigurationAsync(
        TrustedGitHubAppConfiguration configuration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(
        "This trusted host does not support runtime configuration activation.");

    Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
        long installationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default);

    Task<TrustedMergeResult> MergeAsync(
        TrustedMergeRequest request,
        CancellationToken cancellationToken = default);

    Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(
        TrustedWorkspaceSnapshotRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedGitHubAppConfiguration(
    long AppId,
    string PrivateKeyBase64,
    long Revision);

public sealed record TrustedGitHubAppConfigurationStatus(
    bool Configured,
    long? AppId,
    long Revision,
    string? AppSlug,
    string? AppName,
    string? FailureMessage);

public sealed record TrustedMergeRequest(
    Guid OrganizationId,
    Guid RepositoryId,
    Guid PublicationId,
    Guid MergeJobId,
    long InstallationId,
    string Owner,
    string Repository,
    int PullRequestNumber,
    string ExpectedHeadSha,
    string IdempotencyKey);

public sealed record TrustedInstallationDescriptor(
    long InstallationId,
    long AccountId,
    string AccountLogin,
    string AccountType,
    bool Suspended,
    string? SuspendedReason);

public sealed record TrustedRepositoryDescriptor(
    long RepositoryId,
    string Owner,
    string Name,
    string FullName,
    string CloneUrl,
    string DefaultBranch,
    bool IsPrivate,
    bool IsArchived,
    bool IsTemplate);

public sealed record TrustedMergeResult(
    bool Merged,
    bool HeadMatched,
    string? MergeCommitSha,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record TrustedWorkspaceSnapshotRequest(
    long InstallationId,
    long ExternalRepositoryId,
    string Owner,
    string Repository,
    string DefaultBranch,
    Guid WorkspaceId,
    string DeterministicBranch,
    string? ExpectedCommitSha,
    string IdempotencyKey);

public sealed record TrustedWorkspaceSnapshot(
    string WorkspaceKey,
    string BaseCommitSha,
    bool Resumed,
    byte[] Archive,
    string ArtifactSha256,
    int FileCount,
    long TotalBytes);
