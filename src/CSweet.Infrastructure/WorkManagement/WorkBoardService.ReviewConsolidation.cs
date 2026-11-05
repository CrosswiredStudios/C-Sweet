using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkBoardService
{
    /// <summary>An authorized human maps redundant review criteria to existing native stages.
    /// Cancellation is not acceptance; dependents wait for the canonical ticket to complete.</summary>
    public async Task<ConsolidateWorkReviewsResult> ConsolidateReviewsAsync(Guid organizationId, Guid boardId,
        Guid applicationUserId, ConsolidateWorkReviewsRequest request, CancellationToken token = default)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, token);
        if (member.PermissionLevel < OrganizationPermissionLevel.Manager) throw new UnauthorizedAccessException("Review consolidation requires management authority.");
        await RequireAsync(organizationId, member, WorkItemActions.Read, boardId, token);
        var decision = await RequireAsync(organizationId, member, WorkSprintActions.ManageScope, boardId, token);
        await RequireAsync(organizationId, member, WorkItemActions.RevisePlanning, boardId, token);
        if (request.Reviews is not { Count: >= 1 and <= 8 } || request.Reviews.Select(x => x.WorkItemId).Distinct().Count() != request.Reviews.Count ||
            request.Reviews.Any(x => x.WorkItemId == request.CanonicalWorkItemId || x.CanonicalStageKey is not ("quality" or "technical-review")) ||
            request.Reason is not { Length: > 0 and <= 2000 } || request.IdempotencyKey is not { Length: > 0 and <= 64 })
            throw new ArgumentException("Declare distinct redundant reviews, exact native stages and a bounded reason/key.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var key = $"review-consolidation:{member.Id:N}:{request.IdempotencyKey}";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions))));
        var replay = await db.WorkOrchestrationEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.EventType == "reviews.consolidated" && x.IdempotencyKey == key, token);
        if (replay is not null)
        {
            var receipt = JsonSerializer.Deserialize<ConsolidationReceipt>(replay.DataJson, JsonOptions)!;
            if (replay.BoardId != boardId || receipt.Fingerprint != fingerprint) throw new InvalidOperationException("The consolidation key has different terms.");
            // Recover an interrupted/older consolidation's derived parent pins without
            // accepting any newly edited source, criteria, workflow or started aggregate.
            var pinsChanged = await RepairConsolidatedWorkflowPinsAsync(organizationId, request.CanonicalWorkItemId, token);
            var columnsChanged = await RepairConsolidatedReviewColumnsAsync(organizationId,boardId,receipt.Result.CancelledReviewIds,token);
            if (pinsChanged || columnsChanged)
            {
                var sprint = await db.WorkSprintExecutions.SingleAsync(x => x.Id == replay.SprintExecutionId, token);
                sprint.Revision++; sprint.UpdatedAt = DateTimeOffset.UtcNow;
                db.WorkOrchestrationEvents.Add(new() { Id=Guid.NewGuid(), OrganizationId=organizationId, BoardId=boardId,
                    SprintExecutionId=sprint.Id, EventType=pinsChanged ? "reviews.consolidated.references-reconciled" : "reviews.consolidated.columns-reconciled", IdempotencyKey=key+(pinsChanged ? ":pins-v1" : ":columns-v1"),
                    OccurredAt=sprint.UpdatedAt, DataJson=JsonSerializer.Serialize(new { request.CanonicalWorkItemId, actorId=member.Id },JsonOptions) });
                await WorkOrchestrationBoardState.SaveBoardChangesAsync(db,sprint,sprint.UpdatedAt,token);
                if(transaction is not null) await transaction.CommitAsync(token);
            }
            return receipt.Result;
        }
        var board = await db.WorkBoards.SingleAsync(x => x.Id == boardId && x.OrganizationId == organizationId && x.ArchivedAt == null, token);
        var tasks = await db.CoreWorkTasks.Include(x => x.Dependencies).Where(x => x.OrganizationId == organizationId && x.BoardId == boardId).ToListAsync(token);
        if (tasks.Count > 2000) throw new InvalidOperationException("Consolidation exceeds the bounded board graph.");
        var canonical = tasks.Single(x => x.Id == request.CanonicalWorkItemId && x.ArchivedAt == null);
        if (canonical.Revision != request.ExpectedCanonicalRevision) throw new DbUpdateConcurrencyException("The canonical ticket changed.");
        if (canonical.Status == WorkTaskStatus.Cancelled) throw new InvalidOperationException("The canonical ticket is cancelled.");
        var canonicalExecution = await db.WorkItemExecutions.Include(x => x.SprintExecution).Include(x => x.Stages)
            .SingleAsync(x => x.WorkItemId == canonical.Id && x.SprintExecution!.Status == WorkSprintExecutionStatus.Active, token);
        var execution = await db.WorkSprintExecutions.Include(x => x.Items).ThenInclude(x => x.WorkItem)
            .Include(x => x.Items).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts)
            .SingleAsync(x => x.Id == canonicalExecution.SprintExecutionId && x.BoardId == boardId && x.OrganizationId == organizationId, token);
        var ids = request.Reviews.Select(x => x.WorkItemId).ToHashSet();
        // A canonical ticket cannot inherit a dependency on itself, even through another ticket.
        var visited = new HashSet<Guid>();
        bool RequiresReview(Guid id) => visited.Add(id) && tasks.Single(x => x.Id == id).Dependencies.Any(d =>
            ids.Contains(d.DependsOnWorkItemId) || tasks.Any(t => t.Id == d.DependsOnWorkItemId) && RequiresReview(d.DependsOnWorkItemId));
        if (RequiresReview(canonical.Id)) throw new InvalidOperationException("Canonical work depends on a review being consolidated.");
        var cancelledColumn = await ReviewTerminalColumnAsync(boardId,token);
        foreach (var mapping in request.Reviews)
        {
            var review = tasks.Single(x => x.Id == mapping.WorkItemId && x.ArchivedAt == null);
            var item = execution.Items.Single(x => x.WorkItemId == review.Id);
            var planning = JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(review.PlanningSpecificationJson!, JsonOptions)!;
            if (review.Revision != mapping.ExpectedRevision) throw new DbUpdateConcurrencyException("A review ticket changed.");
            if (review.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled ||
                item.Stages.Any(x => x.Attempts.Count != 0 || x.Status is WorkStageExecutionStatus.Running or WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.WaitingForApproval))
                throw new InvalidOperationException("Only unstarted, unfinished review work can be consolidated.");
            if (mapping.AcceptanceCriteria is null || mapping.AcceptanceCriteria.Count == 0 ||
                !planning.AcceptanceCriteria.SequenceEqual(mapping.AcceptanceCriteria, StringComparer.Ordinal) ||
                !canonicalExecution.Stages.Any(x => x.StageKey == mapping.CanonicalStageKey && x.Status != WorkStageExecutionStatus.Cancelled))
                throw new InvalidOperationException("Every exact review criterion must map to an existing canonical native stage.");
        }
        var dependents = tasks.Where(t => !ids.Contains(t.Id) && t.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
            t.Dependencies.Any(d => ids.Contains(d.DependsOnWorkItemId))).ToArray();
        if (dependents.Any(x => x.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForApproval ||
                execution.Items.Where(i => i.WorkItemId == x.Id).SelectMany(i => i.Stages).Any(s => s.Status is WorkStageExecutionStatus.Running or WorkStageExecutionStatus.Dispatching)))
            throw new InvalidOperationException("A dependent is already executing; reach a stopped boundary first.");
        var now = DateTimeOffset.UtcNow;
        var delivery = canonical.DeliverySpecificationJson is null ? null :
            JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(canonical.DeliverySpecificationJson, JsonOptions);
        if (delivery?.DeliveryPlanId is { } planId)
        {
            var plan = await db.WorkDeliveryPlans.Include(x => x.Executions).ThenInclude(x => x.Stages)
                .SingleAsync(x => x.Id == planId && x.OrganizationId == organizationId && x.WorkstreamId == board.WorkstreamId, token);
            var scopes = JsonSerializer.Deserialize<Shared.WorkDeliveryScopeSnapshot[]>(plan.ScopesJson, JsonOptions)!;
            var affected = scopes.Where(x => x.ChildIds.Any(ids.Contains) || x.PredecessorItemIds.Any(ids.Contains)).ToArray();
            foreach (var scope in affected.Concat(scopes.Where(x => x.ChildDeliveryDigests.Keys.Any(id => dependents.Any(d => d.Id == id)))).Distinct())
            {
                if (scope.ChildIds.Any(ids.Contains) && !scope.ChildIds.Contains(canonical.Id) || scope.TicketWorkflow is null ||
                    !tasks.Any(x => x.Id == scope.TicketWorkflow.TicketId && x.Status is not (WorkTaskStatus.Running or WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)) ||
                    plan.Executions.Where(x => x.Scope == scope.Scope && x.WorkItemId == scope.ItemId).Any(x =>
                        x.Status != "WaitingForChildren" || x.CandidateJson is not null || x.AcceptanceJson is not null || x.Stages.Count != 0))
                    throw new InvalidOperationException("Only unstarted aggregate evidence declarations can be consolidated within their existing canonical scope.");
            }
            // Criteria and branch bindings stay intact. Amend only pending evidence declarations;
            // otherwise a cancelled review would remain a hidden gate in the active plan.
            foreach (var scope in affected)
            {
                var workflow = scope.TicketWorkflow! with { EvidenceItemIds = scope.TicketWorkflow!.EvidenceItemIds.Where(x => !ids.Contains(x)).ToArray() };
                var workflowTask = tasks.Single(x => x.Id == workflow.TicketId);
                var planning = JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(workflowTask.PlanningSpecificationJson!, JsonOptions)!;
                planning = planning with { Workflow = workflow.Definition(), DependencyItemIds = planning.DependencyItemIds.Where(x => !ids.Contains(x)).ToArray() };
                workflowTask.PlanningSpecificationJson = JsonSerializer.Serialize(planning, JsonOptions);
                workflowTask.PlanningRevision++; workflowTask.Revision++; workflowTask.UpdatedAt = now;
                scopes[Array.IndexOf(scopes, scope)] = scope with { ChildIds = scope.ChildIds.Where(x => !ids.Contains(x)).ToArray(),
                    ChildPlanningRevisions = scope.ChildPlanningRevisions.Where(x => !ids.Contains(x.Key)).ToDictionary(),
                    ChildDeliveryDigests = scope.ChildDeliveryDigests.Where(x => !ids.Contains(x.Key)).ToDictionary(),
                    PredecessorItemIds = scope.PredecessorItemIds.Where(x => !ids.Contains(x)).ToArray(),
                    TicketWorkflow = workflow, TicketPlanningRevision = workflowTask.PlanningRevision, Planning = planning };
            }
            plan.ScopesJson = JsonSerializer.Serialize(PropagateWorkflowPlanningRevisions(scopes), JsonOptions); plan.Revision++; plan.UpdatedAt = now;
        }
        foreach (var dependent in dependents)
        {
            foreach (var edge in dependent.Dependencies.Where(x => ids.Contains(x.DependsOnWorkItemId)).ToArray())
            { dependent.Dependencies.Remove(edge); db.WorkItemDependencies.Remove(edge); }
            if (!dependent.Dependencies.Any(x => x.DependsOnWorkItemId == canonical.Id))
                dependent.Dependencies.Add(new() { WorkItemId = dependent.Id, DependsOnWorkItemId = canonical.Id });
            if (dependent.PlanningSpecificationJson is { } json)
            {
                var planning = JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(json, JsonOptions)!;
                dependent.PlanningSpecificationJson = JsonSerializer.Serialize(planning with {
                    DependencyItemIds = planning.DependencyItemIds.Select(x => ids.Contains(x) ? canonical.Id : x).Distinct().ToArray() }, JsonOptions);
            }
            if (dependent.DeliverySpecificationJson is { } deliveryJson)
            {
                var specification = JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(deliveryJson, JsonOptions)!;
                dependent.DeliverySpecificationJson = JsonSerializer.Serialize(specification with {
                    DependencyItemIds = specification.DependencyItemIds.Select(x => ids.Contains(x) ? canonical.Id : x).Distinct().ToArray() }, JsonOptions);
            }
            // Only equivalent dependency references change. Accepted scope, criteria, estimates
            // and their planning/assignment revisions are preserved and recorded in the receipt.
            dependent.Revision++; dependent.UpdatedAt = now;
        }
        if (delivery?.DeliveryPlanId is { } updatedPlanId)
        {
            var plan = db.WorkDeliveryPlans.Local.Single(x => x.Id == updatedPlanId);
            var scopes = JsonSerializer.Deserialize<Shared.WorkDeliveryScopeSnapshot[]>(plan.ScopesJson, JsonOptions)!;
            for (var i = 0; i < scopes.Length; i++)
            {
                var digests = scopes[i].ChildDeliveryDigests.ToDictionary();
                foreach (var dependent in dependents.Where(x => digests.ContainsKey(x.Id)))
                {
                    var assignments = await db.WorkItemStageAssignments.AsNoTracking().Where(x => x.WorkItemId == dependent.Id).ToListAsync(token);
                    digests[dependent.Id] = WorkDeliveryService.TaskDeliveryDigest(dependent, assignments);
                }
                scopes[i] = scopes[i] with { ChildDeliveryDigests = digests };
            }
            plan.ScopesJson = JsonSerializer.Serialize(scopes, JsonOptions);
        }
        foreach (var mapping in request.Reviews)
        {
            var review = tasks.Single(x => x.Id == mapping.WorkItemId);
            var item = execution.Items.Single(x => x.WorkItemId == review.Id);
            review.Status = WorkTaskStatus.Cancelled; review.BlockReason = null; review.WaitingReason = null;
            if(cancelledColumn is {} terminalColumn) review.BoardColumnId=terminalColumn;
            review.ResultSummary = $"Consolidated into {canonical.Identifier}, native {mapping.CanonicalStageKey} stage. That stage must establish acceptance. {request.Reason}";
            review.Revision++; review.UpdatedAt = now;
            item.Status = WorkItemExecutionStatus.Cancelled; item.BlockedReason = null; item.CompletedAt = now; item.UpdatedAt = now;
            foreach (var stage in item.Stages.Where(x => x.Status != WorkStageExecutionStatus.Completed))
            { stage.Status = WorkStageExecutionStatus.Cancelled; stage.LastSummary = review.ResultSummary; stage.UpdatedAt = now; }
            AddActivity(organizationId, boardId, review.Id, member, WorkSprintActions.ManageScope, "item.review.consolidated", decision,
                new { canonicalWorkItemId = canonical.Id, mapping.CanonicalStageKey, mapping.AcceptanceCriteria, request.Reason }, now);
        }
        var result = new ConsolidateWorkReviewsResult(canonical.Id, ids.ToArray(), dependents.Select(x => x.Id).ToArray());
        execution.Revision++; execution.UpdatedAt = now;
        db.WorkOrchestrationEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id,
            SprintExecutionId = execution.Id, EventType = "reviews.consolidated", IdempotencyKey = key,
            OccurredAt = now, DataJson = JsonSerializer.Serialize(new ConsolidationReceipt(fingerprint, result), JsonOptions) });
        await WorkOrchestrationBoardState.SaveBoardChangesAsync(db, execution, now, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }
    private Task<Guid?> ReviewTerminalColumnAsync(Guid board,CancellationToken token) => db.WorkBoardColumns.Where(x=>x.BoardId==board &&
        (x.Category==WorkBoardColumnCategory.Cancelled || x.Category==WorkBoardColumnCategory.Done))
        .OrderBy(x=>x.Category==WorkBoardColumnCategory.Cancelled?0:1).ThenBy(x=>x.Position).Select(x=>(Guid?)x.Id).FirstOrDefaultAsync(token);
    private async Task<bool> RepairConsolidatedReviewColumnsAsync(Guid org,Guid board,IReadOnlyList<Guid> ids,CancellationToken token)
    {
        if(await ReviewTerminalColumnAsync(board,token) is not {} column)return false;
        var rows=await db.CoreWorkTasks.Where(x=>x.OrganizationId==org && x.BoardId==board && ids.Contains(x.Id) && x.Status==WorkTaskStatus.Cancelled && x.BoardColumnId!=column).ToListAsync(token);
        foreach(var row in rows){row.BoardColumnId=column;row.Revision++;row.UpdatedAt=DateTimeOffset.UtcNow;}
        return rows.Count!=0;
    }
    internal static Shared.WorkDeliveryScopeSnapshot[] PropagateWorkflowPlanningRevisions(Shared.WorkDeliveryScopeSnapshot[] scopes)
    {
        var revisions=scopes.Where(x=>x.TicketWorkflow is not null && x.TicketPlanningRevision.HasValue).ToDictionary(x=>x.TicketWorkflow!.TicketId,x=>x.TicketPlanningRevision!.Value);
        return scopes.Select(scope=>scope with { ChildPlanningRevisions=scope.ChildPlanningRevisions.ToDictionary(
            x=>x.Key,x=>revisions.TryGetValue(x.Key,out var current)?current:x.Value) }).ToArray();
    }
    private async Task<bool> RepairConsolidatedWorkflowPinsAsync(Guid organization,Guid canonicalId,CancellationToken token)
    {
        var canonical=await db.CoreWorkTasks.SingleAsync(x=>x.Id==canonicalId && x.OrganizationId==organization,token);
        var delivery=JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(canonical.DeliverySpecificationJson??"null",JsonOptions);
        if(delivery?.DeliveryPlanId is not {} id)return false;
        var plan=await db.WorkDeliveryPlans.Include(x=>x.Executions).ThenInclude(x=>x.Stages).SingleAsync(x=>x.Id==id && x.OrganizationId==organization,token);
        var scopes=JsonSerializer.Deserialize<Shared.WorkDeliveryScopeSnapshot[]>(plan.ScopesJson,JsonOptions)!;
        var corrected=PropagateWorkflowPlanningRevisions(scopes);
        if(JsonSerializer.Serialize(scopes,JsonOptions)==JsonSerializer.Serialize(corrected,JsonOptions))return false;
        const string reason="Declared evidence planning changed or was cancelled; reconcile the workflow ticket.";
        foreach(var execution in plan.Executions.Where(x=>x.ScopeRevision==plan.ScopeRevision))
            if(execution.CandidateJson is not null || execution.AcceptanceJson is not null || execution.Stages.Count!=0 ||
                execution.Status!="WaitingForChildren" && !(execution.Status=="Blocked" && execution.BlockedReason==reason))
                throw new InvalidOperationException("Parent reference repair requires unstarted aggregate evidence declarations.");
        foreach(var scope in scopes.Where(x=>x.TicketWorkflow is not null))
        {
            var task=await db.CoreWorkTasks.SingleAsync(x=>x.Id==scope.TicketWorkflow!.TicketId && x.OrganizationId==organization,token);
            if(task.PlanningRevision!=scope.TicketPlanningRevision || task.PlanningSpecificationJson is null ||
                JsonSerializer.Serialize(JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(task.PlanningSpecificationJson,JsonOptions),JsonOptions)!=JsonSerializer.Serialize(scope.Planning,JsonOptions))
                throw new InvalidOperationException("The workflow changed after consolidation; derived pins cannot accept new planning.");
        }
        // Each repair accepts exactly the single revision made by this consolidation.
        for(var i=0;i<scopes.Length;i++)
            foreach(var pair in scopes[i].ChildPlanningRevisions)
                if(corrected[i].ChildPlanningRevisions[pair.Key]!=pair.Value && corrected[i].ChildPlanningRevisions[pair.Key]!=pair.Value+1)
                    throw new InvalidOperationException("Unexpected parent planning revision delta.");
        plan.ScopesJson=JsonSerializer.Serialize(corrected,JsonOptions);plan.Revision++;plan.UpdatedAt=DateTimeOffset.UtcNow;
        foreach(var execution in plan.Executions.Where(x=>x.ScopeRevision==plan.ScopeRevision && x.Status=="Blocked"))
        {execution.Status="WaitingForChildren";execution.BlockedReason=null;execution.Revision++;execution.UpdatedAt=plan.UpdatedAt;}
        return true;
    }
    private sealed record ConsolidationReceipt(string Fingerprint, ConsolidateWorkReviewsResult Result);
}
