using CSweet.Agent.SDK;
using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class TaskDeliveryService
{
    public async Task AdvanceMergeAsync(Guid org, Guid id, ITrustedSourceControlHostClient host, CancellationToken ct)
    {
        TaskDeliveryReview review;
        SourceControlPublication publication;
        SourceControlRepository repository;
        // Persist a started operation before crossing the host boundary. A restart repeats the same
        // provider idempotency key; preferences can revoke only operations that have not started.
        await using (var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            await LockAsync(org, ct);
            review = await db.TaskDeliveryReviews.SingleAsync(x => x.OrganizationId == org && x.Id == id, ct);
            if (review.Status is not ("AwaitingApproval" or "Merging")) return;
            var task = await TaskAsync(org, review.TaskId, ct);
            var epic = await TaskAsync(org, review.EpicId, ct);
            await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock).RequireIfConfiguredAsync(epic, ct);
            if (epic.Status != WorkTaskStatus.Running || task.Status != WorkTaskStatus.WaitingForApproval)
                throw new InvalidOperationException("The task or its epic was stopped. Resume the approved work before merging.");
            publication = await db.SourceControlPublications.SingleAsync(x => x.OrganizationId == org && x.Id == review.PublicationId, ct);
            repository = await db.SourceControlRepositories.Include(x => x.Connection).SingleAsync(x => x.OrganizationId == org && x.Id == review.RepositoryId, ct);
            var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.Id == publication.WorkspaceId, ct);
            if (repository.ArchivedAt is not null || repository.Status != SourceControlRepositoryStatus.Ready ||
                repository.Connection?.Status != SourceControlConnectionStatus.Connected || !repository.IsPrivate ||
                !await db.AgentInstallations.AnyAsync(x => x.Id == review.DeveloperInstallationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct) ||
                !await db.TeamRepositoryPolicies.AnyAsync(x => x.OrganizationId == org && x.TeamId == workspace.TeamId && x.RepositoryId == repository.Id && x.DisabledAt == null, ct) ||
                !await (from member in db.TeamMemberships
                    join employee in db.CoreOrganizationUsers on member.OrganizationUserId equals employee.Id
                    where member.OrganizationId == org && member.TeamId == workspace.TeamId && member.EndedAt == null &&
                        employee.OrganizationId == org && employee.AgentInstallationId == review.DeveloperInstallationId && employee.IsActive && employee.ArchivedAt == null
                    select member.Id).AnyAsync(ct))
                throw new UnauthorizedAccessException("Restore the developer’s active project and repository access before this merge can continue.");
            if (review.Status != "Merging")
            {
                var developer = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == review.DeveloperInstallationId && x.IsActive && x.ArchivedAt == null, ct);
                var manager = await ManagerAsync(org, developer, ct, review.BoardId);
                if (manager.Id != review.ManagerOrganizationUserId)
                {
                    review.ManagerOrganizationUserId = manager.Id; review.ApprovedCommitSha = null;
                    review.ApprovedByOrganizationUserId = null; review.DecisionId = null; review.Revision++;
                    review.UpdatedAt = clock.GetUtcNow();
                    Queue(review, review.DeveloperInstallationId, TaskDeliveryCapabilities.Changed);
                    await db.SaveChangesAsync(ct);
                    if (tx is not null) await tx.CommitAsync(ct);
                    return;
                }
                if (review.QaInstallationId is not null && review.QualityStatus != "Passed") return;
                var policy = await db.TeamRepositoryPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.TeamId == workspace.TeamId && x.RepositoryId == repository.Id && x.DisabledAt == null, ct)
                    ?? throw new UnauthorizedAccessException("Team access to this repository was revoked.");
                if (!await db.TeamMemberships.AnyAsync(x => x.OrganizationId == org && x.TeamId == workspace.TeamId && x.OrganizationUserId == developer.Id && x.EndedAt == null, ct) ||
                    !await db.AgentInstallations.AnyAsync(x => x.Id == review.DeveloperInstallationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct))
                    throw new UnauthorizedAccessException("The developer no longer has project access.");
                if (policy.MergeApprovalMode == TeamMergeApprovalMode.LeadAndAdministratorApproval && manager.PermissionLevel < OrganizationPermissionLevel.Manager)
                    throw new UnauthorizedAccessException("Repository policy requires a manager or administrator approval.");
                if (!(review.ApprovedByOrganizationUserId == manager.Id && review.ApprovedCommitSha == review.CommitSha) && !await AutoApprovedAsync(review, ct)) return;
                if (task.Status != WorkTaskStatus.WaitingForApproval || task.AssignedAgentInstallationId != review.DeveloperInstallationId ||
                    task.AssignmentRevision != workspace.AssignmentRevision || publication.CommitSha != review.CommitSha ||
                    publication.Status is SourceControlPublicationStatus.Failed or SourceControlPublicationStatus.Superseded ||
                    await db.SourceControlPublications.AnyAsync(x => x.WorkspaceId == publication.WorkspaceId && x.CreatedAt > publication.CreatedAt && x.Status != SourceControlPublicationStatus.Failed && x.Status != SourceControlPublicationStatus.Superseded, ct))
                    throw new InvalidOperationException("The task changed after review. Publish and review its current changes.");
                if (repository.ArchivedAt is not null || repository.Status != SourceControlRepositoryStatus.Ready || repository.Connection?.Status != SourceControlConnectionStatus.Connected)
                    throw new InvalidOperationException("Restore repository access before merging this task.");
                review.Status = "Merging"; review.Revision++; review.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            if (tx is not null) await tx.CommitAsync(ct);
        }
        var key = $"task-merge:{review.Id:N}:{review.CommitSha}";
        TrustedMergeResult result;
        if (repository.Connection!.Provider == SourceControlProvider.InternalGit)
        {
            var merged = await host.MergeInternalAsync(new(org, repository.Id, publication.Id, publication.TicketBranch,
                publication.TargetBranch, review.CommitSha, key), ct);
            result = new(merged.Merged, merged.HeadMatched, merged.MergeCommitSha, merged.FailureCode, merged.FailureMessage);
        }
        else if (int.TryParse(publication.PullRequestId, out var number) && repository.Connection.SourceAccessInstallationId is { } provider)
            result = await host.MergeAsync(new(org, repository.Id, publication.Id, review.Id, provider, repository.Owner, repository.Name, number, review.CommitSha, key), ct);
        else throw new InvalidOperationException("This task has no mergeable pull request.");
        var current = await TaskAsync(org, review.TaskId, ct);
        if (result.Merged && result.HeadMatched && !string.IsNullOrWhiteSpace(result.MergeCommitSha))
        {
            review.Status = "Merged"; review.MergeCommitSha = result.MergeCommitSha; review.Failure = null;
            current.BlockReason = null; current.MergeStatus = "Merged"; current.MergeCommitSha = result.MergeCommitSha; current.MergedAt = clock.GetUtcNow();
            publication.Status = SourceControlPublicationStatus.Merged; publication.Revision++; publication.UpdatedAt = clock.GetUtcNow();
        }
        else
        {
            review.Status = "ChangesRequested"; review.Failure = result.FailureMessage ?? "The branch could not merge. Update it against the latest main branch, resolve conflicts, and run tests again.";
            if (review.Failure.Length > 4096) review.Failure = review.Failure[..4096];
            current.Status = WorkTaskStatus.Running; current.MergeStatus = "ChangesRequested"; current.BlockReason = review.Failure;
            current.BoardColumnId = await db.WorkBoardColumns.Where(x => x.BoardId == current.BoardId && x.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.InProgress).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct) ?? current.BoardColumnId;
        }
        review.Revision++; review.UpdatedAt = clock.GetUtcNow(); current.Revision++; current.UpdatedAt = clock.GetUtcNow();
        Queue(review, review.DeveloperInstallationId, TaskDeliveryCapabilities.Changed);
        await QueueUiAsync(current, ct); await db.SaveChangesAsync(ct);
    }
}
