using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Setup;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>
/// Revalidates durable exact-SHA governance and asks the trusted source-control host to merge.
/// This executor never receives provider credentials, calls provider APIs, or executes repo code.
/// </summary>
public sealed class GovernedMergeWorkActionExecutor(
    CSweetDbContext db,
    ITrustedSourceControlHostClient sourceControlHost,
    ISourceControlDecisionSigner decisionSigner,
    TimeProvider timeProvider) : ITrustedWorkActionExecutor
{
    public const string ActionName = "source-control.merge.execute.v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Action => ActionName;

    public async Task<TrustedWorkActionResult> ExecuteAsync(
        TrustedWorkActionContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var itemExecution = await db.WorkItemExecutions
            .Include(x => x.WorkItem)
            .SingleAsync(x => x.Id == context.ItemExecutionId, cancellationToken);
        var item = itemExecution.WorkItem!;
        var publication = await (
            from candidate in db.SourceControlPublications
            join workspace in db.SourceControlWorkspaces.AsNoTracking()
                on new { candidate.OrganizationId, Id = candidate.WorkspaceId }
                equals new { workspace.OrganizationId, workspace.Id }
            where candidate.OrganizationId == context.OrganizationId &&
                  workspace.WorkItemId == context.WorkItemId &&
                  workspace.AssignmentRevision == item.AssignmentRevision &&
                  candidate.Status != SourceControlPublicationStatus.Superseded
            orderby candidate.CreatedAt descending
            select new { Publication = candidate, Workspace = workspace })
            .FirstOrDefaultAsync(cancellationToken);
        if (publication is null)
            return Blocked("Governed merge requires a current source publication.");
        if (publication.Publication.Status ==
            SourceControlPublicationStatus.BranchPublishedExternalMerge)
        {
            return Completed(
                "published_external_merge",
                "The credential-free generic Git branch was published for external review and merge.",
                publication.Publication.CommitSha,
                null,
                publication.Publication.PullRequestUrl);
        }

        var repository = await db.SourceControlRepositories.AsNoTracking()
            .Include(candidate => candidate.Connection)
            .SingleOrDefaultAsync(candidate =>
                candidate.OrganizationId == context.OrganizationId &&
                candidate.Id == publication.Publication.RepositoryId,
                cancellationToken);
        if (repository?.Connection is null ||
            repository.Connection.Provider != SourceControlProvider.GitHub ||
            repository.Connection.Status != SourceControlConnectionStatus.Connected ||
            !repository.Connection.SourceAccessInstallationId.HasValue)
            return Blocked("Governed merge requires an active GitHub Source Access connection.");
        if (!int.TryParse(publication.Publication.PullRequestId, out var pullRequestNumber) ||
            pullRequestNumber <= 0)
            return Blocked("Governed merge requires a provider-confirmed proposed-change identifier.");

        var validation = await db.SourceControlValidations.AsNoTracking()
            .Where(x => x.OrganizationId == context.OrganizationId &&
                        x.PublicationId == publication.Publication.Id &&
                        x.CommitSha == publication.Publication.CommitSha &&
                        x.Status == SourceControlValidationStatus.Passed &&
                        x.SupersededAt == null)
            .OrderByDescending(x => x.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (validation is null)
            return Blocked("Governed merge requires passing QA evidence for the exact current SHA.");

        var policy = await db.TeamRepositoryPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == context.OrganizationId &&
            x.TeamId == publication.Workspace.TeamId &&
            x.RepositoryId == publication.Publication.RepositoryId &&
            x.DisabledAt == null,
            cancellationToken);
        if (policy is null)
            return Blocked("The current team repository policy is unavailable.");
        var authorization = await db.SourceControlMergeAuthorizations.AsNoTracking()
            .Where(x => x.OrganizationId == context.OrganizationId &&
                        x.PublicationId == publication.Publication.Id &&
                        x.CommitSha == publication.Publication.CommitSha &&
                        x.TeamPolicyRevision == policy.Revision &&
                        x.RevokedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.AuthorizedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (authorization is null)
            return Blocked("Governed merge requires an unexpired team-lead authorization for the exact current SHA.");
        if (!decisionSigner.Verify(
                new SourceControlMergeDecision(
                    authorization.OrganizationId,
                    authorization.PublicationId,
                    authorization.CommitSha,
                    authorization.AuthorizedByOrganizationUserId,
                    authorization.TeamPolicyRevision,
                    authorization.AuthorizedAt,
                    authorization.ExpiresAt),
                authorization.DecisionSignature))
            return Blocked("The team-lead merge authorization signature is invalid.");

        var idempotencyKey = $"merge:{publication.Publication.Id:N}:{publication.Publication.CommitSha}";
        var job = await db.SourceControlMergeJobs.SingleOrDefaultAsync(x =>
            x.OrganizationId == context.OrganizationId && x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (job?.Status == SourceControlMergeStatus.Merged &&
            !string.IsNullOrWhiteSpace(job.MergeCommitSha))
        {
            return Completed(
                "merged", "The exact authorized SHA was already merged.",
                publication.Publication.CommitSha, job.MergeCommitSha,
                publication.Publication.PullRequestUrl);
        }
        if (job?.Status == SourceControlMergeStatus.Cancelled)
            return Blocked(job.FailureMessage ?? "The required manager or owner rejected this merge.");
        job ??= new SourceControlMergeJob
        {
            Id = Guid.NewGuid(),
            OrganizationId = context.OrganizationId,
            PublicationId = publication.Publication.Id,
            LeadAuthorizationId = authorization.Id,
            ExpectedHeadSha = publication.Publication.CommitSha,
            ApprovalMode = policy.MergeApprovalMode,
            IdempotencyKey = idempotencyKey,
            Status = policy.MergeApprovalMode == TeamMergeApprovalMode.LeadAuthorizedAutoMerge
                ? SourceControlMergeStatus.Ready
                : SourceControlMergeStatus.AwaitingApproval,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (db.Entry(job).State == EntityState.Detached)
            db.SourceControlMergeJobs.Add(job);
        if (policy.MergeApprovalMode == TeamMergeApprovalMode.LeadAndAdministratorApproval &&
            !job.AdministratorApprovalId.HasValue)
        {
            if (!await db.SourceControlApprovals.AnyAsync(candidate =>
                    candidate.OrganizationId == context.OrganizationId &&
                    candidate.MergeJobId == job.Id,
                    cancellationToken))
            {
                var approval = new SourceControlApproval
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = context.OrganizationId,
                    Kind = SourceControlApprovalKind.Merge,
                    Status = CSweet.Domain.Core.ApprovalStatus.Pending,
                    RequestedByOrganizationUserId = authorization.AuthorizedByOrganizationUserId,
                    MergeJobId = job.Id,
                    IdempotencyKey = $"merge-approval:{job.Id:N}",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.SourceControlApprovals.Add(approval);
                var approvers = await db.CoreOrganizationUsers.AsNoTracking()
                    .Where(candidate => candidate.OrganizationId == context.OrganizationId &&
                                        candidate.IsActive &&
                                        candidate.EmployeeType == EmployeeType.Human &&
                                        candidate.PermissionLevel >= OrganizationPermissionLevel.Manager)
                    .Select(candidate => candidate.Id)
                    .ToListAsync(cancellationToken);
                foreach (var approverId in approvers)
                {
                    db.UserNotifications.Add(new UserNotification
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = context.OrganizationId,
                        RecipientOrganizationUserId = approverId,
                        OriginatingAgentOrganizationUserId = authorization.AuthorizedByOrganizationUserId,
                        Severity = NotificationSeverity.Important,
                        Category = "SourceControlMergeApproval",
                        Title = "Code merge approval needed",
                        Body = $"Review the exact QA-approved version for {repository.Name}.",
                        ActionUri = $"/organizations/{context.OrganizationId:D}/approvals",
                        DeduplicationKey = $"source-control-approval:{approval.Id:N}:{approverId:N}",
                        CreatedAt = now
                    });
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            publication.Publication.Status = SourceControlPublicationStatus.AwaitingAdministratorApproval;
            publication.Publication.UpdatedAt = now;
            publication.Publication.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return Blocked("Governed merge is awaiting the required manager or owner approval.");
        }

        job.Status = SourceControlMergeStatus.Merging;
        job.UpdatedAt = now;
        job.Revision++;
        await db.SaveChangesAsync(cancellationToken);

        TrustedMergeResult result;
        try
        {
            result = await sourceControlHost.MergeAsync(
                new TrustedMergeRequest(
                    context.OrganizationId,
                    publication.Publication.RepositoryId,
                    publication.Publication.Id,
                    job.Id,
                    repository.Connection.SourceAccessInstallationId.Value,
                    repository.Owner,
                    repository.Name,
                    pullRequestNumber,
                    publication.Publication.CommitSha,
                    idempotencyKey),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            job.Status = SourceControlMergeStatus.Failed;
            job.FailureCode = "trusted_host_unavailable";
            job.FailureMessage = exception.Message;
            job.UpdatedAt = timeProvider.GetUtcNow();
            job.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return Blocked(exception.Message);
        }

        now = timeProvider.GetUtcNow();
        if (!result.HeadMatched)
        {
            await InvalidateStaleHeadAsync(
                publication.Publication, authorization.Id, now, cancellationToken);
            job.Status = SourceControlMergeStatus.Superseded;
            job.FailureCode = result.FailureCode ?? "head_changed";
            job.FailureMessage = result.FailureMessage ??
                "The proposed-change head changed after QA and lead authorization.";
            job.UpdatedAt = now;
            job.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return Blocked(job.FailureMessage);
        }
        if (!result.Merged || string.IsNullOrWhiteSpace(result.MergeCommitSha))
        {
            job.Status = SourceControlMergeStatus.Failed;
            job.FailureCode = result.FailureCode ?? "merge_rejected";
            job.FailureMessage = result.FailureMessage ?? "The provider did not confirm the merge.";
            job.UpdatedAt = now;
            job.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return Blocked(job.FailureMessage);
        }

        job.Status = SourceControlMergeStatus.Merged;
        job.MergeCommitSha = result.MergeCommitSha;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        job.Revision++;
        publication.Publication.Status = SourceControlPublicationStatus.Merged;
        publication.Publication.UpdatedAt = now;
        publication.Publication.Revision++;
        item.MergeStatus = "Merged";
        item.MergeCommitSha = result.MergeCommitSha;
        item.MergedAt = now;
        item.MergeAuthorizationGrantId = authorization.Id;
        item.MergeAuthorizationGrantRevision = authorization.TeamPolicyRevision;
        await db.SaveChangesAsync(cancellationToken);
        return Completed(
            "merged", "The exact QA-approved and lead-authorized SHA was merged.",
            publication.Publication.CommitSha, result.MergeCommitSha,
            publication.Publication.PullRequestUrl);
    }

    private async Task InvalidateStaleHeadAsync(
        SourceControlPublication publication,
        Guid authorizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        publication.Status = SourceControlPublicationStatus.Superseded;
        publication.UpdatedAt = now;
        publication.Revision++;
        var validations = await db.SourceControlValidations.Where(x =>
            x.OrganizationId == publication.OrganizationId &&
            x.PublicationId == publication.Id &&
            x.Status != SourceControlValidationStatus.Superseded)
            .ToListAsync(cancellationToken);
        foreach (var validation in validations)
        {
            validation.Status = SourceControlValidationStatus.Superseded;
            validation.SupersededAt = now;
            validation.UpdatedAt = now;
        }
        var authorization = await db.SourceControlMergeAuthorizations
            .SingleAsync(x => x.Id == authorizationId, cancellationToken);
        authorization.RevokedAt = now;
        authorization.RevocationReason = "The proposed-change head changed.";
    }

    private static TrustedWorkActionResult Completed(
        string outcomeCode,
        string summary,
        string sourceCommitSha,
        string? mergeCommitSha,
        string? pullRequestUrl) => new(
        Shared.WorkExecutionDispositions.Completed,
        outcomeCode,
        summary,
        JsonSerializer.SerializeToElement(
            new { sourceCommitSha, mergeCommitSha, pullRequestUrl }, JsonOptions),
        []);

    private static TrustedWorkActionResult Blocked(string summary) => new(
        Shared.WorkExecutionDispositions.Blocked,
        "blocked",
        summary,
        JsonSerializer.SerializeToElement(new { }, JsonOptions),
        [summary]);
}
