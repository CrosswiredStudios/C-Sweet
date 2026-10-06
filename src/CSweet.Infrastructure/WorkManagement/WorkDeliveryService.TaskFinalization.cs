using System.Data;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    public async Task<WorkDeliveryPlanResponse> FinalizeTaskAsync(Guid org, Guid actorId, FinalizeWorkItemDeliveryRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var planId = request.Delivery.DeliveryPlanId ?? throw new ArgumentException("Bind the task to its explicit delivery plan.");
        var plan = await GetAuthorizedAsync(org, actorId, planId, WorkDeliveryCapabilities.Configure, ct);
        var member = await MemberAsync(org, actorId, ct);
        var boardManager = await db.WorkBoards.Where(x => x.Id == request.BoardId && x.OrganizationId == org).Select(x => x.ManagerOrganizationUserId).SingleAsync(ct);
        if (member.EmployeeType != EmployeeType.Human || member.Id != boardManager)
            throw new UnauthorizedAccessException("This operation requires the assigned human board manager; agents use the versioned work-item finalization capability.");
        await AuthorizeBoardAsync(org, actorId, request.BoardId, CSweet.Contracts.WorkManagement.WorkItemActions.FinalizeDelivery, ct);
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "finalize-task", request, ct);
        if (replay is not null) return Response(replay);
        if (plan.Status is not ("Draft" or "Paused")) throw new InvalidOperationException("Pause delivery before changing task staffing.");
        var item = await db.CoreWorkTasks.Include(x => x.StageAssignments).Include(x => x.Approvals)
            .SingleAsync(x => x.OrganizationId == org && x.BoardId == request.BoardId && x.Id == request.ItemId, ct);
        if (!item.IsExecutable || item.Revision != request.ExpectedRevision || item.ArchivedAt.HasValue || item.Status == WorkTaskStatus.Completed)
            throw new DbUpdateConcurrencyException("The current executable task cannot be finalized with this revision.");
        if (item.Approvals.Any(x => x.PlanningRevision == item.PlanningRevision && x.Status != "Approved"))
            throw new InvalidOperationException("Obtain every required discipline planning approval first.");
        if (await db.WorkItemExecutions.AnyAsync(x => x.WorkItemId == item.Id && x.Status != WorkItemExecutionStatus.Completed && x.Status != WorkItemExecutionStatus.Cancelled, ct))
            throw new InvalidOperationException("Stop the existing sprint execution before replacing its delivery assignments.");
        var story = Scopes(plan).SingleOrDefault(x => x.Scope == "Story" && x.ItemId == item.ParentWorkTaskId && x.BoardId == item.BoardId && x.ChildIds.Contains(item.Id))
            ?? throw new UnauthorizedAccessException("The task must belong to the manager-approved story and epic scope.");
        var planning = Decode<WorkItemPlanningSpecification>(item.PlanningSpecificationJson ?? "null") ?? throw new InvalidOperationException("Accepted task planning is required.");
        if (!planning.Requirements.SequenceEqual(request.Delivery.Requirements) || !planning.AcceptanceCriteria.SequenceEqual(request.Delivery.AcceptanceCriteria) ||
            !(planning.Constraints ?? []).SequenceEqual(request.Delivery.Constraints ?? []) || !planning.DependencyItemIds.Order().SequenceEqual(request.Delivery.DependencyItemIds.Order()))
            throw new InvalidOperationException("Task finalization must preserve the exact accepted planning.");
        var policy = await db.WorkOrchestrationPolicyRevisions.Include(x => x.Stages).SingleOrDefaultAsync(x => x.BoardId == request.BoardId && x.IsPublished &&
            db.WorkOrchestrationPolicies.Any(p => p.PublishedRevisionId == x.Id), ct) ?? throw new InvalidOperationException("Publish the hierarchical task workflow first.");
        if (!policy.Stages.Any(x => x.PlatformAction == HierarchicalWorkflows.TaskIntegrationAction) ||
            request.StageAssignments.Select(x => x.StageKey).Distinct().Count() != request.StageAssignments.Count ||
            request.StageAssignments.Any(x => !policy.Stages.Any(s => s.Key == x.StageKey)))
            throw new ArgumentException("Use unique assignments belonging to the current hierarchical workflow.");
        item.AccountableOrganizationUserId = request.AccountableOrganizationUserId;
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.Id == request.AccountableOrganizationUserId && x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct))
            throw new ArgumentException("The accountable employee must be active.");
        item.DeliverySpecificationJson = Encode(request.Delivery);
        db.WorkItemStageAssignments.RemoveRange(item.StageAssignments); item.StageAssignments.Clear();
        foreach (var assignment in request.StageAssignments)
        {
            var entity = new WorkItemStageAssignment { Id = Guid.NewGuid(), OrganizationId = org, BoardId = request.BoardId, WorkItemId = item.Id,
                StageKey = assignment.StageKey, PrincipalKind = Enum.Parse<WorkOrchestrationPrincipalKind>(assignment.PrincipalKind), OrganizationUserId = assignment.OrganizationUserId,
                AgentInstallationId = assignment.AgentInstallationId, PlatformAction = assignment.PlatformAction,
                RequirementsJson = assignment.Requirements is null ? null : Encode(assignment.Requirements),
                SelectionEvidenceJson = assignment.SelectionEvidence is null ? null : Encode(assignment.SelectionEvidence), CreatedAt = clock.GetUtcNow() };
            item.StageAssignments.Add(entity); db.WorkItemStageAssignments.Add(entity);
        }
        // Validate the staged values in the same transaction. The shared validator
        // reads persisted task assignments, so save provisionally before activation checks.
        item.Revision++; item.UpdatedAt = clock.GetUtcNow(); await SaveDeliveryAsync(ct);
        await ValidateTaskDeliveryAsync(plan, story with { ChildDeliveryDigests = new Dictionary<Guid, string>() }, item, ct);
        plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        Receipt(plan, actorId, request.IdempotencyKey, "finalize-task", request); Queue(plan, "task.finalized");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Response(plan);
    }
}
