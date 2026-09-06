using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class AgentWorkspaceBroker
{
    public async Task<AgentBrokerWorkspaceOperationResult> ExecuteAsync(AgentBrokerWorkspaceOperationRequest request,
        string publicBaseUrl, CancellationToken cancellationToken = default)
    {
        if (request.Operation is not ("inspect" or "publish" or "refresh" or "cleanup") ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("Invalid workspace operation.");
        var workspace = await db.SourceControlWorkspaces.AsNoTracking().Include(w => w.Repository!).ThenInclude(r => r.Connection)
            .SingleOrDefaultAsync(w => w.OrganizationId == request.OrganizationId && w.Id == request.WorkspaceId &&
                w.RepositoryId == request.RepositoryId && w.WorkItemId == request.WorkItemId &&
                w.AssignmentRevision == request.AssignmentRevision && w.WorkspaceKey == request.WorkspaceKey, cancellationToken)
            ?? throw new UnauthorizedAccessException("Workspace operation does not match its persisted assignment.");
        if (workspace.Status is not (SourceControlWorkspaceStatus.Ready or SourceControlWorkspaceStatus.Published))
            throw new InvalidOperationException("Workspace is not available for this operation.");
        var repository = workspace.Repository!;
        if (!repository.IsPrivate || repository.ArchivedAt is not null || repository.Status != SourceControlRepositoryStatus.Ready ||
            repository.Connection?.Provider is not (SourceControlProvider.InternalGit or SourceControlProvider.GitHub) || repository.Connection.Status != SourceControlConnectionStatus.Connected)
            throw new InvalidOperationException("An active supported repository is required for this operation.");
        if (!await db.CoreWorkTasks.AsNoTracking().AnyAsync(w => w.Id == workspace.WorkItemId && w.OrganizationId == workspace.OrganizationId &&
            w.AssignmentRevision == workspace.AssignmentRevision && w.AssignedAgentInstallationId == workspace.AgentInstallationId, cancellationToken) ||
            !await db.AgentInstallations.AsNoTracking().AnyAsync(i => i.Id == workspace.AgentInstallationId && i.IsEnabled &&
                i.BusinessId == workspace.OrganizationId.ToString("D"), cancellationToken))
            throw new UnauthorizedAccessException("Workspace assignment is stale.");
        await AuthorizeWorkspaceTeamAsync(workspace, cancellationToken);
        var lease = new WorkspaceVolumeLease(workspace.OrganizationId, workspace.AgentInstallationId, workspace.Id,
            workspace.WorkItemId, workspace.AssignmentRevision);
        WorkspaceVolumeExport export;
        try { export = await volumes.ExportAsync(lease, cancellationToken); }
        catch (WorkspaceSnapshotUnavailableException) when (request.Operation == "cleanup")
        {
            // A previous cleanup may have removed the snapshot before its response was delivered.
            await volumes.RemoveAsync(lease, cancellationToken);
            return new("Removed", workspace.BaseCommitSha, [], "", Removed: true);
        }
        var operation = request.Operation == "cleanup" ? "inspect" : request.Operation;
        string? githubReviewUrl = null;
        async Task<InternalGitSnapshotResult> ApplyAsync(string verb, string baseSha)
        {
            var snapshot = new InternalGitSnapshotOperation(workspace.OrganizationId, workspace.RepositoryId, workspace.Id, verb, baseSha,
                workspace.BranchName, repository.DefaultBranch, request.IdempotencyKey, export.Archive, export.Manifest.Sha256,
                export.Manifest.FileCount, export.Manifest.TotalBytes, request.CommitMessage);
            if (repository.Connection.Provider == SourceControlProvider.InternalGit) return await gitHost.ApplyInternalSnapshotAsync(snapshot, cancellationToken);
            if (repository.Connection.SourceAccessInstallationId is not > 0 || !long.TryParse(repository.ExternalRepositoryId, out var externalId) || externalId <= 0)
                throw new InvalidOperationException("The GitHub source access identity is unavailable.");
            var github = await gitHost.ApplyGitHubSnapshotAsync(new(repository.Connection.SourceAccessInstallationId.Value, externalId, repository.Owner,
                repository.Name, snapshot, request.ProposedChangeTitle, request.ProposedChangeBody), cancellationToken);
            if (github.Snapshot.Status == "Published" && string.IsNullOrWhiteSpace(github.PullRequestUrl))
                throw new InvalidOperationException("GitHub did not confirm a pull request for the published commit.");
            githubReviewUrl = github.PullRequestUrl; return github.Snapshot;
        }
        var result = await ApplyAsync(operation, workspace.BaseCommitSha);
        if (request.Operation == "cleanup")
        {
            if (request.RetainOnFailure && result.ChangedFiles.Count != 0)
                return new("Retained", workspace.BaseCommitSha, result.ChangedFiles, result.DiffSummary, RetainUntil: DateTimeOffset.UtcNow.AddDays(7));
            await volumes.RemoveAsync(lease, cancellationToken);
            return new("Removed", workspace.BaseCommitSha, [], "", Removed: true);
        }
        if (request.Operation == "refresh" && result.LatestTargetSha is { } latest && latest != workspace.BaseCommitSha)
        {
            if (result.ChangedFiles.Count > 0)
            {
                // Import can finish before the caller persists the new base. Recognize that exact snapshot on retry.
                var atLatest = await ApplyAsync("inspect", latest);
                if (atLatest.ChangedFiles.Count == 0) return new("Refreshed", latest, [], "");
                return new("Conflict", workspace.BaseCommitSha, result.ChangedFiles,
                    "The remote branch changed while the workspace has edits. Publish or resolve those edits before refreshing.");
            }
            var snapshot = repository.Connection.Provider == SourceControlProvider.InternalGit
                ? await gitHost.PrepareInternalWorkspaceAsync(new(workspace.OrganizationId, repository.Id, workspace.Id,
                    repository.DefaultBranch, workspace.BranchName, latest, request.IdempotencyKey), cancellationToken)
                : await gitHost.PrepareWorkspaceAsync(new(repository.Connection.SourceAccessInstallationId!.Value, long.Parse(repository.ExternalRepositoryId!), repository.Owner, repository.Name,
                    repository.DefaultBranch, workspace.Id, workspace.BranchName, latest, request.IdempotencyKey), cancellationToken);
            await using var archive = new MemoryStream(snapshot.Archive, writable: false);
            await volumes.ImportAsync(lease, archive, new(snapshot.ArtifactSha256, snapshot.FileCount, snapshot.TotalBytes), cancellationToken);
            return new("Refreshed", latest, [], "");
        }
        var url = request.Operation == "publish" && result.Status == "Published"
            ? $"{publicBaseUrl.TrimEnd('/')}/organizations/{workspace.OrganizationId:D}/source-control?repository={repository.Id:D}&reference={Uri.EscapeDataString("refs/heads/" + workspace.BranchName)}"
            : null;
        return new(result.Status, result.BaseSha, result.ChangedFiles, result.DiffSummary, result.CommitSha, workspace.BranchName, githubReviewUrl ?? url, Provider: repository.Connection.Provider.ToString());
    }
    private async Task AuthorizeWorkspaceTeamAsync(SourceControlWorkspace workspace, CancellationToken cancellationToken)
    {
        if (!await db.TeamRepositoryPolicies.AsNoTracking().AnyAsync(p => p.OrganizationId == workspace.OrganizationId &&
            p.TeamId == workspace.TeamId && p.RepositoryId == workspace.RepositoryId && p.DisabledAt == null, cancellationToken))
            throw new UnauthorizedAccessException("Team repository access has been revoked.");
        var employee = await db.CoreOrganizationUsers.AsNoTracking().Where(u => u.OrganizationId == workspace.OrganizationId &&
            u.AgentInstallationId == workspace.AgentInstallationId && u.IsActive).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken);
        if (employee is null || !await db.TeamMemberships.AsNoTracking().AnyAsync(m => m.OrganizationId == workspace.OrganizationId &&
            m.TeamId == workspace.TeamId && m.OrganizationUserId == employee.Value && m.EndedAt == null, cancellationToken) ||
            !await db.OrganizationTeams.AsNoTracking().AnyAsync(t => t.Id == workspace.TeamId && t.OrganizationId == workspace.OrganizationId &&
                t.ArchivedAt == null, cancellationToken))
            throw new UnauthorizedAccessException("Active membership in the repository team is required.");
    }

}
