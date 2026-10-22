using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    // An explicit manager/owner control accepts the current canonical text, never a wake or
    // automatic retry. Old executions remain immutable evidence; acceptance must run again.
    private async Task ReconcileCurrentPlanningAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        if (plan.Executions.Any(e => e.Promotions.Any(p => p.Status == "Completed")) ||
            plan.Executions.SelectMany(e => e.Stages).SelectMany(s => s.Attempts)
                .Any(a => a.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
            throw new InvalidOperationException("Drain active delivery work before reconciling unpromoted scope.");
        var scopes = Scopes(plan);
        var ids = scopes.SelectMany(s => s.ChildIds.Concat(s.PredecessorItemIds)
            .Append(s.TicketWorkflow!.TicketId).Concat(s.ItemId is { } id ? [id] : [])).Distinct().ToArray();
        var items = await db.CoreWorkTasks.Include(t => t.StageAssignments).Where(t => ids.Contains(t.Id) &&
            t.OrganizationId == plan.OrganizationId && t.ArchivedAt == null).ToDictionaryAsync(t => t.Id, ct);
        if (items.Count != ids.Length || items.Values.Any(t => t.Status == WorkTaskStatus.Cancelled))
            throw new InvalidOperationException("Reconciliation cannot remove or replace declared work.");
        if (await db.WorkExecutionAttempts.AnyAsync(a => a.StageExecution!.ItemExecution != null &&
            ids.Contains(a.StageExecution.ItemExecution.WorkItemId) &&
            (a.Status == WorkExecutionAttemptStatus.Pending || a.Status == WorkExecutionAttemptStatus.Running), ct))
            throw new InvalidOperationException("Drain task writers before reconciling delivery planning.");
        var refreshed = new List<WorkDeliveryScopeSnapshot>();
        foreach (var scope in scopes)
        {
            var ticket = items[scope.TicketWorkflow!.TicketId];
            var planning = Decode<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!);
            // Only canonical text/revisions may change here. Graph, source references,
            // workflow steps, assignment identities and branch bindings remain explicit.
            if (ticket.BoardId != scope.BoardId || ticket.TypeKey != WorkItemTypeKeys.WorkflowTicketV1 ||
                Encode(scope.Planning with { Requirements = planning.Requirements,
                    AcceptanceCriteria = planning.AcceptanceCriteria, Constraints = planning.Constraints }) != Encode(planning) ||
                Encode(planning.Workflow) != Encode(scope.TicketWorkflow.Definition()) ||
                !planning.DependencyItemIds.SequenceEqual(scope.PredecessorItemIds))
                throw new InvalidOperationException("Changed workflow structure requires a new declared plan.");
            foreach (var id in scope.ChildDeliveryDigests.Keys)
            {
                var task = items[id];
                var delivery = Decode<WorkItemDeliverySpecification>(task.DeliverySpecificationJson!);
                var current = Decode<WorkItemPlanningSpecification>(task.PlanningSpecificationJson!);
                if (task.BoardId != scope.BoardId || delivery.DeliveryPlanId != plan.Id ||
                    !delivery.Requirements.SequenceEqual(current.Requirements) ||
                    !delivery.AcceptanceCriteria.SequenceEqual(current.AcceptanceCriteria) ||
                    !(delivery.Constraints ?? []).SequenceEqual(current.Constraints ?? []))
                    throw new InvalidOperationException("Finalize current canonical task criteria before reconciling delivery.");
            }
            refreshed.Add(scope with { Planning = planning, TicketPlanningRevision = ticket.PlanningRevision,
                PlanningRevision = scope.ItemId is { } source ? items[source].PlanningRevision : ticket.PlanningRevision,
                ChildPlanningRevisions = scope.ChildPlanningRevisions.Keys.ToDictionary(id => id, id => items[id].PlanningRevision),
                ChildDeliveryDigests = scope.ChildDeliveryDigests.Keys.ToDictionary(id => id,
                    id => TaskDeliveryDigest(items[id], items[id].StageAssignments.ToList())) });
        }
        var prior = plan.ScopesJson;
        plan.ScopesJson = Encode(refreshed);
        try { await ValidateCurrentScopeAsync(plan, ct); }
        catch { plan.ScopesJson = prior; throw; }
        var now = clock.GetUtcNow();
        plan.ScopeRevision++;
        foreach (var old in plan.Executions.Where(e => e.Status != "Superseded").ToArray())
        {
            foreach (var stage in old.Stages)
            {
                if (stage.Status != WorkStageExecutionStatus.Completed) stage.Status = WorkStageExecutionStatus.Cancelled;
                await RevokeDeliveryGrantsAsync(old, stage, ct);
            }
            old.Status = "Superseded";
            var next = new WorkDeliveryExecution { Id = Guid.NewGuid(), PlanId = plan.Id, Scope = old.Scope,
                WorkItemId = old.WorkItemId, BoardId = old.BoardId, ScopeRevision = plan.ScopeRevision,
                CreatedAt = now, UpdatedAt = now };
            plan.Executions.Add(next); db.WorkDeliveryExecutions.Add(next);
        }
    }
}
