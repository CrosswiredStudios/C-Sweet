using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed partial class GitWorkspaceCapabilityHandler
{
    // Called only after the current assignment, repository and team have been authorized.
    // An unpublished revision must carry its predecessor's code into the new task branch.
    // Once integrated, the story is authoritative (and may include other tasks' changes).
    internal static async Task<string?> ResolveReworkBaseAsync(
        CSweetDbContext db, Guid organizationId, Guid repositoryId, Guid workItemId,
        long assignmentRevision, string targetBranch, CancellationToken cancellationToken)
    {
        var candidate = await (
            from publication in db.SourceControlPublications.AsNoTracking()
            join workspace in db.SourceControlWorkspaces.AsNoTracking()
                on new { publication.OrganizationId, Id = publication.WorkspaceId }
                equals new { workspace.OrganizationId, workspace.Id }
            where publication.OrganizationId == organizationId &&
                  publication.RepositoryId == repositoryId && workspace.RepositoryId == repositoryId &&
                  workspace.WorkItemId == workItemId &&
                  workspace.AssignmentRevision == assignmentRevision - 1 &&
                  publication.TargetBranch == targetBranch &&
                  publication.Status != SourceControlPublicationStatus.Superseded &&
                  publication.Status != SourceControlPublicationStatus.Failed
            orderby publication.CreatedAt descending
            select publication).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null || candidate.Status == SourceControlPublicationStatus.Merged)
            return null;
        if (await db.WorkTaskIntegrationReceipts.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.RepositoryId == repositoryId &&
                x.WorkItemId == workItemId && x.PublicationId == candidate.Id && x.Status == "Completed",
                cancellationToken))
            return null;
        return ValidateCommitSha(candidate.CommitSha);
    }
}
