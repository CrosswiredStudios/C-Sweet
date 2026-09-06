using CSweet.Application.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public interface IAgentWorkspaceBroker
{
    Task<AgentBrokerWorkspaceOperationResult> ExecuteAsync(AgentBrokerWorkspaceOperationRequest request,
        string publicBaseUrl, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Workspace operation is unavailable.");
    Task<AgentBrokerWorkspacePrepareResult> PrepareAsync(
        AgentBrokerWorkspacePrepareRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Core-only resolver between the agent-facing broker and GitHost. Provider installation IDs and
/// repository coordinates are introduced only after every opaque assignment identifier has been
/// matched against persisted state, then removed again before the response reaches AgentHost.
/// </summary>
public sealed partial class AgentWorkspaceBroker(
    CSweetDbContext db,
    ITrustedSourceControlHostClient gitHost,
    IWorkspaceVolumeBridge volumes) : IAgentWorkspaceBroker
{
    public async Task<AgentBrokerWorkspacePrepareResult> PrepareAsync(
        AgentBrokerWorkspacePrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await db.SourceControlWorkspaces.AsNoTracking()
            .Include(candidate => candidate.Repository!)
            .ThenInclude(repository => repository.Connection)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == request.WorkspaceId &&
                candidate.OrganizationId == request.OrganizationId &&
                candidate.AgentInstallationId == request.AgentInstallationId &&
                candidate.RepositoryId == request.RepositoryId &&
                candidate.WorkItemId == request.WorkItemId &&
                candidate.AssignmentRevision == request.AssignmentRevision,
                cancellationToken)
            ?? throw new UnauthorizedAccessException(
                "The workspace broker request does not match persisted assignment state.");
        if (workspace.Status != SourceControlWorkspaceStatus.Preparing)
            throw new InvalidOperationException("The source-control workspace is not awaiting materialization.");
        if (!string.Equals(workspace.BranchName, request.DeterministicBranch, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The requested branch does not match the assigned workspace.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("The workspace idempotency key is invalid.");

        var assignmentIsCurrent = await db.CoreWorkTasks.AsNoTracking().AnyAsync(candidate =>
            candidate.Id == request.WorkItemId &&
            candidate.OrganizationId == request.OrganizationId &&
            candidate.AssignedAgentInstallationId == request.AgentInstallationId &&
            candidate.AssignmentRevision == request.AssignmentRevision,
            cancellationToken);
        var installationIsCurrent = await db.AgentInstallations.AsNoTracking().AnyAsync(candidate =>
            candidate.Id == request.AgentInstallationId &&
            candidate.IsEnabled &&
            candidate.BusinessId == request.OrganizationId.ToString("D"),
            cancellationToken);
        if (!assignmentIsCurrent || !installationIsCurrent)
            throw new UnauthorizedAccessException("The workspace broker assignment is stale.");

        var repository = workspace.Repository
            ?? throw new InvalidOperationException("The assigned repository is unavailable.");
        var connection = repository.Connection
            ?? throw new InvalidOperationException("The assigned source-control connection is unavailable.");
        if (repository.Status != SourceControlRepositoryStatus.Ready ||
            repository.ArchivedAt is not null ||
            !repository.IsPrivate ||
            connection.Status != SourceControlConnectionStatus.Connected ||
            (connection.Provider != SourceControlProvider.InternalGit &&
                (connection.Provider != SourceControlProvider.GitHub || connection.SourceAccessInstallationId is not > 0)))
            throw new InvalidOperationException("The assigned private repository is not ready.");

        await AuthorizeWorkspaceTeamAsync(workspace, cancellationToken);

        long externalRepositoryId = 0;
        if (connection.Provider == SourceControlProvider.GitHub &&
            (!long.TryParse(repository.ExternalRepositoryId, out externalRepositoryId) || externalRepositoryId <= 0))
            throw new InvalidOperationException("The assigned GitHub repository identity is unavailable.");

        var snapshot = connection.Provider == SourceControlProvider.InternalGit
            ? await gitHost.PrepareInternalWorkspaceAsync(new(request.OrganizationId, repository.Id, workspace.Id,
                repository.DefaultBranch, workspace.BranchName, request.ExpectedCommitSha, request.IdempotencyKey), cancellationToken)
            : await gitHost.PrepareWorkspaceAsync(
            new TrustedWorkspaceSnapshotRequest(
                connection.SourceAccessInstallationId!.Value,
                externalRepositoryId,
                repository.Owner,
                repository.Name,
                repository.DefaultBranch,
                workspace.Id,
                workspace.BranchName,
                request.ExpectedCommitSha,
                request.IdempotencyKey),
            cancellationToken);
        if (request.ExpectedCommitSha is not null &&
            !string.Equals(snapshot.BaseCommitSha, request.ExpectedCommitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHost returned a source revision other than the exact assignment.");

        await using var archive = new MemoryStream(snapshot.Archive, writable: false);
        var expectedManifest = new WorkspaceArtifactManifest(
            snapshot.ArtifactSha256,
            snapshot.FileCount,
            snapshot.TotalBytes);
        await volumes.ImportAsync(
            new WorkspaceVolumeLease(
                request.OrganizationId,
                request.AgentInstallationId,
                request.WorkspaceId,
                request.WorkItemId,
                request.AssignmentRevision),
            archive,
            expectedManifest,
            cancellationToken);
        return new AgentBrokerWorkspacePrepareResult(
            snapshot.WorkspaceKey,
            $"/workspace/{request.WorkItemId:N}/{request.AssignmentRevision}",
            snapshot.BaseCommitSha,
            snapshot.Resumed);
    }
}
