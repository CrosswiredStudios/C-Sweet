using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Independent Technical Review authorizes task integration; QA subsequently tests its exact story commit.</summary>
public sealed class TaskIntegrationWorkActionExecutor(CSweetDbContext db, ITrustedSourceControlHostClient host,
    TimeProvider clock) : ITrustedWorkActionExecutor
{
    public const string ActionName = "source-control.task.integrate.v1";
    public string Action => ActionName;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<TrustedWorkActionResult> ExecuteAsync(TrustedWorkActionContext context, CancellationToken ct = default)
    {
        try
        {
            var execution = await db.WorkItemExecutions.Include(x => x.WorkItem).Include(x => x.Stages).ThenInclude(x => x.Attempts)
                .Include(x => x.SprintExecution).SingleAsync(x => x.Id == context.ItemExecutionId, ct);
            var item = execution.WorkItem!;
            if (execution.SprintExecution?.Status != WorkSprintExecutionStatus.Active || context.OrganizationId != item.OrganizationId ||
                context.WorkItemId != item.Id || context.BoardId != item.BoardId || execution.CurrentStageKey != "task-integration")
                throw new UnauthorizedAccessException("Task integration requires the current authorized sprint stage.");
            var (plan, story) = await WorkDeliveryTaskAuthorization.RequireAsync(db, item, ct);
            if (story is null) throw new InvalidOperationException("Artifact tasks do not require Git integration.");
            var publication = await (from candidate in db.SourceControlPublications join workspace in db.SourceControlWorkspaces
                on candidate.WorkspaceId equals workspace.Id where candidate.OrganizationId == item.OrganizationId && workspace.WorkItemId == item.Id &&
                workspace.AssignmentRevision == item.AssignmentRevision && candidate.Status != SourceControlPublicationStatus.Superseded
                orderby candidate.CreatedAt descending select candidate).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Publish the exact task candidate before Technical Review.");
            if (publication.RepositoryId != story.RepositoryId || publication.TargetBranch != story.SourceBranch)
                throw new UnauthorizedAccessException("The publication targets a branch outside its story binding.");
            var review = execution.Stages.LastOrDefault(x => x.Traversal == execution.Traversal && x.StageKey == "technical-review" &&
                x.Status == WorkStageExecutionStatus.Completed && x.LastOutcomeCode == "approved")
                ?? throw new InvalidOperationException("Independent Technical Review must approve this task candidate.");
            var authors = execution.Stages.Where(x => x.StageKey is "development" or "specialist-execution").ToList();
            if (authors.Any(x => x.OrganizationUserId.HasValue && x.OrganizationUserId == review.OrganizationUserId ||
                x.AgentInstallationId.HasValue && x.AgentInstallationId == review.AgentInstallationId))
                throw new InvalidOperationException("An author cannot approve their own integration.");
            var outcome = JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(review.Attempts.Last(x => x.Status == WorkExecutionAttemptStatus.Completed).ResultJson!, Json)!;
            var sourceSha = outcome.Evidence.SingleOrDefault(x => x.Kind == "commit")?.Value;
            var targetSha = outcome.Evidence.SingleOrDefault(x => x.Kind == "target-commit")?.Value;
            if (sourceSha != publication.CommitSha || string.IsNullOrWhiteSpace(targetSha))
                throw new InvalidOperationException("Technical Review must bind the exact published source and current story target commits.");
            var receipt = await db.WorkTaskIntegrationReceipts.SingleOrDefaultAsync(x => x.PublicationId == publication.Id, ct);
            if (receipt?.Status == "Completed") return Completed(receipt.CandidateCommitSha);
            var repository = await db.SourceControlRepositories.AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == publication.RepositoryId && x.OrganizationId == item.OrganizationId, ct);
            if (repository.ArchivedAt.HasValue || repository.Status != SourceControlRepositoryStatus.Ready || repository.Connection?.Status != SourceControlConnectionStatus.Connected)
                throw new InvalidOperationException("The assigned repository is unavailable.");
            if (!await db.TeamRepositoryPolicies.AnyAsync(x => x.OrganizationId == item.OrganizationId && x.RepositoryId == repository.Id && x.DisabledAt == null &&
                db.WorkBoards.Any(b => b.Id == item.BoardId && b.TeamId == x.TeamId), ct)) throw new UnauthorizedAccessException("The task team repository grant was revoked.");
            var binding = new WorkDeliveryBranchBinding(repository.Id, WorkExecutionScopes.Task, item.Id, publication.TicketBranch, publication.TargetBranch);
            var current = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository, binding, "inspect",
                $"integration-inspect:{publication.Id:N}", sourceSha, targetSha), ct);
            if (current.SourceCommitSha != sourceSha)
                throw new InvalidOperationException("The published task branch changed; publish and independently review its current candidate.");
            var approvedTarget = targetSha;
            targetSha = current.TargetCommitSha;
            var targetChanged = approvedTarget != targetSha;
            if (targetChanged && receipt is not null)
                throw new InvalidOperationException("The Story changed after an integration receipt was prepared; reconcile its native promotion receipt before retrying.");
            var key = $"task-integration:{publication.Id:N}:{sourceSha}:{targetSha}";
            if (receipt is null)
            {
                var candidate = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository, binding, "receipt", key, sourceSha, targetSha), ct);
                if (!candidate.Promoted) candidate = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository, binding, "candidate", key, sourceSha, targetSha), ct);
                if (candidate.ErrorCode == "integration_conflicts")
                {
                    var rework = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository, binding,
                        "rework", key + ":rework", sourceSha, targetSha), ct);
                    if (rework.ReworkCommitSha is not { Length: 40 } reworkSha)
                        throw new InvalidOperationException(rework.Error ?? "The integration conflict workspace could not be prepared.");
                    var summary = "Integration conflicts require author rework. The new workspace contains the latest Story code and " +
                        "the previous task changes with conflict markers. Resolve every conflict while preserving both features, " +
                        "rerun validation, and publish a replacement candidate for fresh technical review, integration and exact-commit QA. " +
                        "Conflicting files: " + string.Join(", ", rework.ConflictPaths);
                    return new(WorkExecutionDispositions.Completed, "integration_conflicts", summary.Length <= 4096 ? summary : summary[..4093] + "...",
                        JsonSerializer.SerializeToElement(new { publicationId = publication.Id, repositoryId = repository.Id,
                            sourceCommitSha = sourceSha, targetCommitSha = targetSha, reworkCommitSha = reworkSha,
                            targetBranch = publication.TargetBranch, conflictPaths = rework.ConflictPaths,
                            assignmentRevision = item.AssignmentRevision }), []);
                }
                if (candidate.CandidateCommitSha is null) throw new InvalidOperationException(candidate.Error ?? "The task integration candidate could not be prepared.");
                if (targetChanged)
                    return new(WorkExecutionDispositions.Completed, "target_changed",
                        "The Story advanced after approval. Independently review the same published source against the current Story before integration.",
                        JsonSerializer.SerializeToElement(new { sourceCommitSha = sourceSha, targetCommitSha = targetSha,
                            approvedTargetCommitSha = approvedTarget, publicationId = publication.Id }), []);
                receipt = new() { Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, PlanId = plan.Id, ScopeRevision = plan.ScopeRevision,
                    ItemExecutionId = execution.Id, WorkItemId = item.Id, RepositoryId = repository.Id, PublicationId = publication.Id,
                    SourceCommitSha = sourceSha, TargetCommitSha = targetSha, CandidateCommitSha = candidate.CandidateCommitSha, CreatedAt = clock.GetUtcNow() };
                db.WorkTaskIntegrationReceipts.Add(receipt); await db.SaveChangesAsync(ct);
            }
            if (receipt.SourceCommitSha != sourceSha || receipt.TargetCommitSha != targetSha || receipt.ScopeRevision != plan.ScopeRevision)
                throw new InvalidOperationException("The prepared task integration no longer matches the approved review and scope.");
            var result = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository, binding, "promote", key,
                sourceSha, targetSha, receipt.CandidateCommitSha), ct);
            receipt.Status = result.Promoted ? "Completed" : "Failed"; receipt.Error = result.Error;
            if (!result.Promoted) { await db.SaveChangesAsync(ct); throw new InvalidOperationException(result.Error ?? "The provider did not confirm task integration."); }
            receipt.CompletedAt = clock.GetUtcNow(); publication.Status = SourceControlPublicationStatus.Merged; publication.UpdatedAt = clock.GetUtcNow(); publication.Revision++;
            item.MergeCommitSha = receipt.CandidateCommitSha; item.MergeStatus = "Merged"; item.MergedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct); return Completed(receipt.CandidateCommitSha);
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
        { return new(WorkExecutionDispositions.Blocked, "blocked", error.Message, JsonSerializer.SerializeToElement(new { }), [error.Message]); }
    }
    private static TrustedWorkActionResult Completed(string sha) => new(WorkExecutionDispositions.Completed, "merged",
        "The independently reviewed task was integrated into its story; QA must test this exact commit.",
        JsonSerializer.SerializeToElement(new { mergeCommitSha = sha }), []);
}
