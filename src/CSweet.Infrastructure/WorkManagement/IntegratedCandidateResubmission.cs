using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public static class IntegratedCandidateResubmission
{
    public const string EventType = "candidate.resubmitted";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<bool> ResumeCarriedCandidateAsync(CSweetDbContext db,
        ITrustedSourceControlHostClient? host, WorkStageExecution author, Guid manager,
        DateTimeOffset now, CancellationToken ct)
    {
        var current = author.ItemExecution!;
        var item = current.WorkItem!;
        if (author.StageKey is not ("development" or "specialist-execution") ||
            string.IsNullOrWhiteSpace(item.MergeCommitSha)) return false;
        var prior = await db.WorkItemExecutions.AsNoTracking().Where(e => e.WorkItemId == item.Id &&
            e.Id != current.Id && e.Status == WorkItemExecutionStatus.Cancelled && e.CurrentStageKey == "quality" &&
            e.SprintExecution!.Status == WorkSprintExecutionStatus.Cancelled &&
            e.SprintExecution.BoardId == current.SprintExecution!.BoardId)
            .OrderByDescending(e => e.UpdatedAt).FirstOrDefaultAsync(ct);
        if (prior is null || !await db.WorkTaskIntegrationReceipts.AnyAsync(r => r.ItemExecutionId == prior.Id &&
            r.WorkItemId == item.Id && r.Status == "Completed" && r.CandidateCommitSha == item.MergeCommitSha, ct)) return false;
        // The current branch, publication, plan, team grants and independent QA assignment
        // are all checked again. Prior QA is never carried forward as a verdict.
        var candidate = await RequireAsync(db, host, item, ct);
        await QueueQaAsync(db, author, candidate, manager, $"carryover-qa:{current.Id:N}",
            "Scope was amended while QA was stopped. Preserve the exact integrated candidate and evaluate all current criteria in fresh independent QA.", now, ct);
        return true;
    }

    public static async Task<SourceControlPublication?> CurrentPublicationAsync(CSweetDbContext db, WorkTask item, CancellationToken ct)
    {
        var latest = await (from publication in db.SourceControlPublications
            join workspace in db.SourceControlWorkspaces on publication.WorkspaceId equals workspace.Id
            where publication.OrganizationId == item.OrganizationId && workspace.OrganizationId == item.OrganizationId &&
                workspace.WorkItemId == item.Id && publication.Status != SourceControlPublicationStatus.Superseded
            orderby publication.CreatedAt descending
            select new { Publication = publication, workspace.AssignmentRevision }).FirstOrDefaultAsync(ct);
        if (latest is null) return null;
        if (latest.AssignmentRevision == item.AssignmentRevision) return latest.Publication;
        var events = await db.WorkOrchestrationEvents.AsNoTracking().Where(entry => entry.OrganizationId == item.OrganizationId &&
            entry.BoardId == item.BoardId && db.WorkItemExecutions.Any(execution => execution.Id == entry.ItemExecutionId && execution.WorkItemId == item.Id) && entry.EventType == EventType)
            .OrderByDescending(entry => entry.OccurredAt).Take(1).ToListAsync(ct);
        var binding = events.FirstOrDefault()?.DataJson is { } data ? JsonSerializer.Deserialize<Binding>(data, Json) : null;
        if (binding is null || binding.AssignmentRevision != item.AssignmentRevision || binding.PlanningRevision != item.PlanningRevision ||
            binding.PublicationId != latest.Publication.Id || binding.IntegratedCommitSha != item.MergeCommitSha) return null;
        var delivery = JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(item.DeliverySpecificationJson ?? "null", Json);
        return delivery?.DeliveryPlanId is { } planId && await db.WorkDeliveryPlans.AnyAsync(plan => plan.Id == planId && plan.OrganizationId == item.OrganizationId &&
            plan.Status == "Active" && plan.ScopeRevision == binding.ScopeRevision, ct) &&
            await db.WorkTaskIntegrationReceipts.AnyAsync(receipt => receipt.Id == binding.IntegrationReceiptId &&
                receipt.OrganizationId == item.OrganizationId && receipt.WorkItemId == item.Id && receipt.PlanId == planId && receipt.PublicationId == binding.PublicationId &&
                receipt.Status == "Completed" && receipt.CandidateCommitSha == binding.IntegratedCommitSha, ct)
            ? latest.Publication : null;
    }

    internal static async Task<IntegratedCandidateResponse> RequireAsync(CSweetDbContext db,
        ITrustedSourceControlHostClient? host, WorkTask item, CancellationToken ct)
    {
        var (plan, story) = await WorkDeliveryTaskAuthorization.RequireAsync(db, item, ct);
        if (story is null || host is null) throw new InvalidOperationException("Integrated code candidate inspection is unavailable.");
        var publication = await (from candidate in db.SourceControlPublications
            join workspace in db.SourceControlWorkspaces on candidate.WorkspaceId equals workspace.Id
            where candidate.OrganizationId == item.OrganizationId && workspace.OrganizationId == item.OrganizationId &&
                workspace.WorkItemId == item.Id && candidate.Status != SourceControlPublicationStatus.Superseded
            orderby candidate.CreatedAt descending select candidate).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("This ticket has no published candidate.");
        var receipt = await db.WorkTaskIntegrationReceipts.SingleOrDefaultAsync(entry => entry.OrganizationId == item.OrganizationId &&
            entry.WorkItemId == item.Id && entry.PlanId == plan.Id && entry.PublicationId == publication.Id && entry.Status == "Completed", ct);
        if (receipt is null || publication.Status != SourceControlPublicationStatus.Merged || receipt.RepositoryId != story.RepositoryId ||
            publication.RepositoryId != story.RepositoryId || publication.TargetBranch != story.SourceBranch ||
            receipt.SourceCommitSha != publication.CommitSha || receipt.CandidateCommitSha != item.MergeCommitSha)
            throw new InvalidOperationException("The latest publication has no matching completed integration. New changes require independent Technical Review and integration.");
        var repository = await db.SourceControlRepositories.AsNoTracking().Include(entry => entry.Connection)
            .SingleAsync(entry => entry.Id == story.RepositoryId && entry.OrganizationId == item.OrganizationId, ct);
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(entry => entry.Id == item.BoardId && entry.OrganizationId == item.OrganizationId, ct);
        if (board.ArchivedAt.HasValue || repository.ArchivedAt.HasValue || repository.Status != SourceControlRepositoryStatus.Ready ||
            repository.Connection?.Status != SourceControlConnectionStatus.Connected ||
            !await db.TeamRepositoryPolicies.AnyAsync(policy => policy.OrganizationId == item.OrganizationId && policy.RepositoryId == repository.Id &&
                policy.TeamId == board.TeamId && policy.DisabledAt == null, ct))
            throw new UnauthorizedAccessException("The current team repository access is unavailable.");
        var current = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository,
            new(repository.Id, Shared.WorkExecutionScopes.Task, item.Id, publication.TicketBranch, publication.TargetBranch),
            "inspect", $"resubmit-inspect:{item.Id:N}:{item.AssignmentRevision}", publication.CommitSha, receipt.CandidateCommitSha), ct);
        if (current.Error is not null || current.ErrorCode is not null || current.SourceCommitSha != publication.CommitSha || current.TargetCommitSha != receipt.CandidateCommitSha)
            throw new InvalidOperationException("The task or Story branch advanced. Do not resubmit a stale integrated candidate; review and integrate the current changes.");
        return new(publication.Id, receipt.Id, repository.Id, publication.CommitSha, receipt.CandidateCommitSha, item.Revision);
    }

    internal static async Task<IReadOnlyList<Shared.WorkExecutionEvidence>> QaEvidenceAsync(CSweetDbContext db, WorkStageExecution stage, CancellationToken ct)
    {
        if (stage.StageKey != "quality") return [];
        var item = stage.ItemExecution!.WorkItem!;
        var entry = await db.WorkOrchestrationEvents.AsNoTracking().Where(entry => entry.OrganizationId == item.OrganizationId &&
            entry.ItemExecutionId == stage.ItemExecutionId && entry.EventType == EventType)
            .OrderByDescending(entry => entry.OccurredAt).FirstOrDefaultAsync(ct);
        var binding = entry is null ? null : JsonSerializer.Deserialize<Binding>(entry.DataJson, Json);
        if (binding is null || binding.AssignmentRevision != item.AssignmentRevision || binding.PlanningRevision != item.PlanningRevision ||
            (await CurrentPublicationAsync(db, item, ct))?.Id != binding.PublicationId) return [];
        return [new("integrated-candidate-resubmission.v1", "Unchanged candidate returned for fresh independent QA; no verdict granted",
            JsonSerializer.Serialize(new { binding.PublicationId, binding.IntegrationReceiptId, binding.IntegratedCommitSha,
                binding.PlanningRevision, binding.AssignmentRevision, binding.Reason,
                instruction = "Evaluate all current approved criteria on this exact commit. Prior findings and observations are historical evidence, not a current QA verdict." }, Json), "application/json")];
    }

    internal static async Task QueueQaAsync(CSweetDbContext db, WorkStageExecution author,
        IntegratedCandidateResponse candidate, Guid actorId, string key, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var item = author.ItemExecution!;
        var sprint = item.SprintExecution!;
        if (sprint.Status != WorkSprintExecutionStatus.Active || item.CurrentStageKey != author.StageKey || item.Traversal != author.Traversal ||
            author.StageKey is not ("specialist-execution" or "development"))
            throw new InvalidOperationException("Only the current author rework stage in an active sprint can resubmit an unchanged candidate.");
        var policy = await db.WorkOrchestrationPolicyRevisions.Include(entry => entry.Stages).Include(entry => entry.Transitions)
            .SingleAsync(entry => entry.Id == sprint.PolicyRevisionId, ct);
        var integration = policy.Stages.SingleOrDefault(entry => entry.Key == "task-integration" && entry.PlatformAction == TaskIntegrationWorkActionExecutor.ActionName);
        var qa = policy.Stages.SingleOrDefault(entry => entry.Key == "quality");
        if (integration is null || qa is null || !policy.Transitions.Any(entry => entry.FromStageKey == integration.Key && entry.OutcomeCode == "merged" && entry.ToStageKey == qa.Key))
            throw new InvalidOperationException("This workflow does not support integrated candidate QA resubmission.");
        if (item.Stages.Any(entry => entry.Traversal == item.Traversal && entry.StageKey == integration.Key))
            throw new InvalidOperationException("This traversal already contains an integration stage. Refresh the current workflow before resubmitting.");
        var snapshot = JsonSerializer.Deserialize<List<WorkOrchestrationService.AssignmentSnapshot>>(sprint.AssignmentSnapshotJson, Json)!;
        var assignments = snapshot.Where(entry => entry.WorkItemId == item.WorkItemId).Select(entry => new WorkItemStageAssignment
        {
            StageKey = entry.StageKey, PrincipalKind = entry.PrincipalKind, OrganizationUserId = entry.OrganizationUserId,
            AgentInstallationId = entry.AgentInstallationId, PlatformAction = entry.PlatformAction,
            RequirementsJson = entry.RequirementsJson, SelectionEvidenceJson = entry.SelectionEvidenceJson
        }).ToList();
        var reviewer = assignments.SingleOrDefault(entry => entry.StageKey == qa.Key);
        if (reviewer is null || item.Stages.Where(entry => entry.StageKey is "development" or "specialist-execution").Any(entry =>
            reviewer.AgentInstallationId == entry.AgentInstallationId && entry.AgentInstallationId.HasValue ||
            reviewer.OrganizationUserId == entry.OrganizationUserId && entry.OrganizationUserId.HasValue))
            throw new InvalidOperationException("Resubmission requires an independently assigned QA reviewer.");
        var planId = await db.WorkTaskIntegrationReceipts.Where(receipt => receipt.Id == candidate.IntegrationReceiptId).Select(receipt => receipt.PlanId).SingleAsync(ct);
        var plan = await db.WorkDeliveryPlans.AsNoTracking().SingleAsync(entry => entry.Id == planId, ct);
        var binding = new Binding(candidate.PublicationId, candidate.IntegrationReceiptId, candidate.IntegratedCommitSha,
            item.WorkItem!.AssignmentRevision, item.WorkItem.PlanningRevision, plan.ScopeRevision, actorId, reason, candidate.ItemRevision);
        db.WorkOrchestrationEvents.Add(new WorkOrchestrationEvent
        {
            Id = Guid.NewGuid(), OrganizationId = sprint.OrganizationId, BoardId = sprint.BoardId,
            SprintExecutionId = sprint.Id, ItemExecutionId = item.Id, StageExecutionId = author.Id,
            EventType = EventType, IdempotencyKey = key, DataJson = JsonSerializer.Serialize(binding, Json), OccurredAt = now
        });
        author.Status = WorkStageExecutionStatus.Completed; author.CompletedAt = now; author.UpdatedAt = now;
        author.LastError = null; author.LastSummary = reason; author.LastOutcomeCode = "integrated-candidate-resubmitted";
        var retained = WorkOrchestrationService.CreateStageExecution(item, integration, assignments, actorId, now);
        retained.Status = WorkStageExecutionStatus.Completed; retained.CompletedAt = now; retained.LastOutcomeCode = "merged";
        retained.LastSummary = "Reusing the recorded integration receipt; no new merge or approval was performed.";
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = retained.Id, Attempt = 1,
            IdempotencyKey = key + ":integration", Status = WorkExecutionAttemptStatus.Completed, CreatedAt = now, CompletedAt = now };
        attempt.ResultJson = JsonSerializer.Serialize(new Shared.WorkExecutionOutcomeV1(retained.Id, attempt.Id,
            Shared.WorkExecutionDispositions.Completed, "merged", retained.LastSummary,
            JsonSerializer.SerializeToElement(new { mergeCommitSha = candidate.IntegratedCommitSha, candidate.IntegrationReceiptId }), [], []), Json);
        retained.Attempts.Add(attempt); db.WorkStageExecutions.Add(retained);
        var next = WorkOrchestrationService.CreateStageExecution(item, qa, assignments, actorId, now);
        db.WorkStageExecutions.Add(next);
        item.WorkItem.Status = WorkTaskStatus.Assigned;
        if (qa.ColumnId.HasValue) item.WorkItem.BoardColumnId = qa.ColumnId;
        item.WorkItem.BlockReason = null; item.WorkItem.WaitingReason = null; item.WorkItem.ClaimEventId = null; item.WorkItem.ClaimExpiresAt = null;
        item.WorkItem.Revision++; item.WorkItem.UpdatedAt = now; item.UpdatedAt = now;
        WorkOrchestrationBoardState.SynchronizeAgentCard(policy, next, now);
        sprint.Revision++; sprint.UpdatedAt = now;
    }

    private sealed record Binding(Guid PublicationId, Guid IntegrationReceiptId, string IntegratedCommitSha,
        long AssignmentRevision, long PlanningRevision, long ScopeRevision, Guid ActorId, string Reason, long ItemRevision);
}
