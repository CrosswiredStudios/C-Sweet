using CSweet.Agent.SDK;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Narrow client boundary to CSweet.GitHost. Requests contain only Core identifiers and bounded
/// content; the trusted host resolves provider credentials independently and never executes code.
/// </summary>
public interface ITrustedGitHostClient
{
    Task<GitWorkspaceLockResult> LocksAsync(TrustedWorkspaceOperationRequest request, string operation, string? path, string? id, string? cursor, CancellationToken ct) =>
        throw new InvalidOperationException("Workspace locks are unavailable.");
    Task<TrustedWorkspaceMaterialization> PrepareAsync(
        TrustedWorkspacePrepareRequest request,
        CancellationToken cancellationToken);

    Task<TrustedWorkspaceRefresh> RefreshAsync(
        TrustedWorkspaceOperationRequest request,
        CancellationToken cancellationToken);

    Task<GitWorkspaceInspection> InspectAsync(
        TrustedWorkspaceOperationRequest request,
        CancellationToken cancellationToken);

    Task<TrustedWorkspacePublication> PublishAsync(
        TrustedWorkspacePublishRequest request,
        CancellationToken cancellationToken);

    Task<GitWorkspaceCleanupResult> CleanupAsync(
        TrustedWorkspaceCleanupRequest request,
        CancellationToken cancellationToken);
}

public sealed record TrustedWorkspacePrepareRequest(
    Guid OrganizationId,
    Guid AgentInstallationId,
    Guid RepositoryId,
    Guid WorkspaceId,
    Guid WorkItemId,
    long AssignmentRevision,
    string DeterministicBranch,
    string? ExpectedCommitSha,
    string IdempotencyKey);

public sealed record TrustedWorkspaceMaterialization(
    string WorkspaceKey,
    string AgentWorkspacePath,
    string BaseCommitSha,
    bool Resumed);

public sealed record TrustedWorkspaceOperationRequest(
    Guid OrganizationId,
    Guid RepositoryId,
    Guid WorkspaceId,
    string WorkspaceKey,
    Guid WorkItemId,
    long AssignmentRevision,
    string IdempotencyKey);

public sealed record TrustedWorkspaceRefresh(
    string Status,
    string BaseCommitSha,
    IReadOnlyList<GitWorkspaceConflict> Conflicts);

public sealed record TrustedWorkspacePublishRequest(
    TrustedWorkspaceOperationRequest Workspace,
    string CommitMessage,
    string ProposedChangeTitle,
    string ProposedChangeBody,
    IReadOnlyList<GitValidationResult> Validations);

public sealed record TrustedWorkspacePublication(
    string Provider,
    string DeliveryKind,
    string BranchName,
    string CommitSha,
    Uri? PullRequestUrl,
    IReadOnlyList<string>? ChangedFiles = null,
    string? DiffSummary = null);

public sealed record TrustedWorkspaceCleanupRequest(
    TrustedWorkspaceOperationRequest Workspace,
    bool RetainOnFailure);

/// <summary>
/// Secure default used until CSweet.GitHost is configured. It deliberately has no local Git or
/// credential fallback.
/// </summary>
public sealed class UnavailableTrustedGitHostClient : ITrustedGitHostClient
{
    private const string Message =
        "The trusted GitHost service is not configured. Source-control work is blocked without exposing credentials.";

    public Task<TrustedWorkspaceMaterialization> PrepareAsync(
        TrustedWorkspacePrepareRequest request,
        CancellationToken cancellationToken) => throw Unavailable();

    public Task<TrustedWorkspaceRefresh> RefreshAsync(
        TrustedWorkspaceOperationRequest request,
        CancellationToken cancellationToken) => throw Unavailable();

    public Task<GitWorkspaceInspection> InspectAsync(
        TrustedWorkspaceOperationRequest request,
        CancellationToken cancellationToken) => throw Unavailable();

    public Task<TrustedWorkspacePublication> PublishAsync(
        TrustedWorkspacePublishRequest request,
        CancellationToken cancellationToken) => throw Unavailable();

    public Task<GitWorkspaceCleanupResult> CleanupAsync(
        TrustedWorkspaceCleanupRequest request,
        CancellationToken cancellationToken) => throw Unavailable();

    private static InvalidOperationException Unavailable() => new(Message);
}
