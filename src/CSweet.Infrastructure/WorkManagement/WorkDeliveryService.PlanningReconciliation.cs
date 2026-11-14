using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    // An explicit manager/owner control accepts the current canonical text, never a wake or
    // automatic retry. Old executions remain immutable evidence; acceptance must run again.
    private async Task ReconcileCurrentPlanningAsync(WorkDeliveryPlan plan, CancellationToken ct,
        IReadOnlyList<WorkPlanningRevisionChange>? approvedChanges = null,
        IReadOnlyList<WorkDeliveryTaskAssignment>? preservedAssignments = null,
        IReadOnlyList<WorkDeliveryOriginalInput>? preservedInputs = null)
    {
        if (plan.Executions.Any(e => e.Promotions.Any(p => p.Status is not ("Failed" or "Cancelled"))) ||
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
        if (approvedChanges is not null)
        {
            if (approvedChanges.Select(c => c.ItemId).Distinct().Count() != approvedChanges.Count)
                throw new ArgumentException("Declare each approved planning change once.");
            var pins = scopes.SelectMany(s => s.ChildPlanningRevisions
                .Concat(new Dictionary<Guid,long> { [s.TicketWorkflow!.TicketId] = s.TicketPlanningRevision ?? s.PlanningRevision })
                .Concat(s.ItemId is { } source ? new Dictionary<Guid,long> { [source] = s.PlanningRevision } : new Dictionary<Guid,long>()))
                .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Value).Distinct().ToArray());
            foreach (var change in approvedChanges)
                if (!pins.TryGetValue(change.ItemId, out var prior) || prior.Length != 1 || prior[0] != change.FromRevision ||
                    !items.TryGetValue(change.ItemId, out var item) || item.PlanningRevision != change.ToRevision || change.ToRevision <= change.FromRevision)
                    throw new InvalidOperationException("The approved planning delta does not match the pinned and canonical revisions.");
            foreach (var (id, prior) in pins)
                if (prior.Length != 1 || items[id].PlanningRevision != prior[0] &&
                    !approvedChanges.Any(c => c.ItemId == id && c.FromRevision == prior[0] && c.ToRevision == items[id].PlanningRevision))
                    throw new InvalidOperationException("Unapproved planning drift requires its own accountable decision.");
            if (plan.Findings.Any(f => f.Status == "Open"))
                throw new InvalidOperationException("Resolve product findings through the retained correction or authorized disposition before reconciling inputs.");
        }
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
                if (approvedChanges is not null && TaskDeliveryDigest(task,task.StageAssignments.ToList())!=scope.ChildDeliveryDigests[id])
                {
                    if (!approvedChanges.Any(c=>c.ItemId==id))
                        throw new InvalidOperationException("Unapproved task delivery or staffing drift requires its own decision.");
                    var original=scope.ChildInputs?.SingleOrDefault(a=>a.ItemId==id)??preservedInputs?.SingleOrDefault(a=>a.ItemId==id);
                    if(original is null)throw new InvalidOperationException("Retain the original delivery inputs so the saved digest can verify legacy reconciliation.");
                    var historical=new WorkTask {DeliverySpecificationJson=original.DeliverySpecificationJson,AccountableOrganizationUserId=original.AccountableOrganizationUserId};
                    var historicalAssignments=original.Stages.Select(a=>new WorkItemStageAssignment {StageKey=a.StageKey,
                        PrincipalKind=Enum.Parse<WorkOrchestrationPrincipalKind>(a.PrincipalKind),OrganizationUserId=a.OrganizationUserId,AgentInstallationId=a.AgentInstallationId,
                        PlatformAction=a.PlatformAction,RequirementsJson=a.Requirements is {} r?Encode(r):null,SelectionEvidenceJson=a.SelectionEvidence is {} e?Encode(e):null}).ToArray();
                    if(TaskDeliveryDigest(historical,historicalAssignments)!=scope.ChildDeliveryDigests[id])
                        throw new InvalidOperationException("The supplied original input does not match the accepted delivery digest.");
                    var priorDelivery=Decode<WorkItemDeliverySpecification>(original.DeliverySpecificationJson);
                    if(Encode(priorDelivery with {Requirements=delivery.Requirements,AcceptanceCriteria=delivery.AcceptanceCriteria,Constraints=delivery.Constraints})!=Encode(delivery))
                        throw new InvalidOperationException("Changed delivery operations or topology require their own declared successor plan.");
                    if (original.AccountableOrganizationUserId!=task.AccountableOrganizationUserId || Encode(original.Stages.OrderBy(a=>a.StageKey))!=Encode(TaskAssignments(task).OrderBy(a=>a.StageKey)))
                        throw new InvalidOperationException("Planning reconciliation must preserve the exact accepted task assignments.");
                }
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
                    id => TaskDeliveryDigest(items[id], items[id].StageAssignments.ToList())),
                ChildInputs=scope.ChildDeliveryDigests.Keys.Select(id=>new WorkDeliveryOriginalInput(id,items[id].DeliverySpecificationJson!,
                    items[id].AccountableOrganizationUserId,TaskAssignments(items[id]))).ToArray() });
        }
        var priorScopes = plan.ScopesJson;
        plan.ScopesJson = Encode(refreshed);
        try { await ValidateCurrentScopeAsync(plan, ct); }
        catch { plan.ScopesJson = priorScopes; throw; }
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

    private static IReadOnlyList<WorkStageAssignment> TaskAssignments(WorkTask task) => task.StageAssignments.Select(a=>
        new WorkStageAssignment(a.StageKey,a.PrincipalKind.ToString(),a.OrganizationUserId,a.AgentInstallationId,a.PlatformAction) {
            Requirements=a.RequirementsJson is {} r?Decode<WorkAssignmentRequirements>(r):null,
            SelectionEvidence=a.SelectionEvidenceJson is {} s?Decode<WorkAssignmentSelectionEvidence>(s):null }).ToArray();
}
