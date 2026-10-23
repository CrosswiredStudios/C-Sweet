using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrationService
{
    private async Task RequireDefectScopeAuthorityAsync(OrganizationUser member, WorkDeliveryPlan plan, CancellationToken ct)
    {
        if (member.PermissionLevel != OrganizationPermissionLevel.Owner && member.Id != plan.ManagerOrganizationUserId)
            throw new UnauthorizedAccessException("The release manager or organization owner must authorize adding a corrective Bug to delivery scope.");
        if (authorization is null || !(await authorization.AuthorizeAsync(plan.OrganizationId,
            member.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
            member.AgentInstallationId ?? member.Id, Shared.WorkDeliveryCapabilities.Configure,
            GrantScopeKind.Workstream, plan.WorkstreamId, ct)).Allowed)
            throw new UnauthorizedAccessException("Current project delivery configuration authority is required, including for replay.");
    }

    private async Task<Shared.WorkItemTypeDefinition> RequireRegisteredBugAsync(WorkBoard board, Guid parentId, CancellationToken ct)
    {
        var project = await db.Workstreams.AsNoTracking().SingleAsync(x => x.Id == board.WorkstreamId, ct);
        var definition = await db.WorkstreamProfileDefinitions.AsNoTracking().Where(x => x.Key == project.ProfileKey &&
            x.Version == project.ProfileVersion && x.DefinitionDigest == project.ProfileDefinitionDigest && x.DefaultBoardProfileKey == board.ProfileKey)
            .Select(x => x.DefinitionJson).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The approved project profile must declare an executable Bug type.");
        var parent = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == parentId && x.OrganizationId == board.OrganizationId && x.BoardId == board.Id, ct);
        return WorkstreamProfileWorkTypeCatalog.Read(definition, board.ProfileKey).SingleOrDefault(x =>
            x.Kind == "Bug" && x.ExecutionMode == "Executable" && x.PermittedParentTypeKeys.Contains(parent.TypeKey))
            ?? throw new InvalidOperationException("The project must declare one compatible executable Bug type.");
    }

    private async Task IncludeCorrectiveBugAsync(WorkDeliveryPlan plan, Shared.WorkDeliveryScopeSnapshot[] scopes,
        WorkTask bug, WorkTask qa, DateTimeOffset now, CancellationToken ct)
    {
        var changed = new Dictionary<Guid, WorkTask> { [qa.Id] = qa, [bug.Id] = bug };
        var revised = new List<Shared.WorkDeliveryScopeSnapshot>();
        foreach (var scope in scopes)
        {
            if (scope.Scope != "Story" || scope.ItemId != bug.ParentWorkTaskId) { revised.Add(scope); continue; }
            if (scope.TicketWorkflow is not { } workflow) throw new InvalidOperationException("Declare the Story's workflow ticket first.");
            var ticket = await db.CoreWorkTasks.SingleAsync(x => x.Id == workflow.TicketId && x.OrganizationId == plan.OrganizationId, ct);
            if (ticket.Status is WorkTaskStatus.Completed or WorkTaskStatus.Running or WorkTaskStatus.Cancelled ||
                await db.WorkItemApprovals.AnyAsync(x => x.WorkItemId == ticket.Id, ct))
                throw new InvalidOperationException("The Story workflow must be stopped and require no additional planning approvals.");
            var planning = JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!, JsonOptions)!;
            var nextWorkflow = workflow with { EvidenceItemIds = workflow.EvidenceItemIds.Append(bug.Id).ToArray() };
            var nextPlanning = planning with { DependencyItemIds = planning.DependencyItemIds.Append(bug.Id).ToArray(), Workflow = nextWorkflow.Definition() };
            ticket.PlanningSpecificationJson = JsonSerializer.Serialize(nextPlanning, JsonOptions);
            ticket.PlanningRevision++; ticket.Revision++; ticket.UpdatedAt = now;
            db.WorkItemDependencies.Add(new() { WorkItemId = ticket.Id, DependsOnWorkItemId = bug.Id });
            changed[ticket.Id] = ticket;
            revised.Add(scope with { Planning = nextPlanning, TicketPlanningRevision = ticket.PlanningRevision,
                ChildIds = scope.ChildIds.Append(bug.Id).ToArray(), TicketWorkflow = nextWorkflow,
                PredecessorItemIds = scope.PredecessorItemIds.Append(bug.Id).ToArray(),
                ChildPlanningRevisions = scope.ChildPlanningRevisions.Append(new KeyValuePair<Guid, long>(bug.Id, 1)).ToDictionary(x => x.Key, x => x.Value),
                ChildDeliveryDigests = scope.ChildDeliveryDigests.Append(new KeyValuePair<Guid, string>(bug.Id,
                    WorkDeliveryService.TaskDeliveryDigest(bug, bug.StageAssignments.ToList()))).ToDictionary(x => x.Key, x => x.Value) });
        }
        plan.ScopesJson = JsonSerializer.Serialize(revised.Select(scope => scope with {
            ChildPlanningRevisions = scope.ChildPlanningRevisions.ToDictionary(x => x.Key, x => changed.TryGetValue(x.Key, out var task) ? task.PlanningRevision : x.Value),
            ChildDeliveryDigests = scope.ChildDeliveryDigests.ToDictionary(x => x.Key,
                x => changed.TryGetValue(x.Key, out var task) ? WorkDeliveryService.TaskDeliveryDigest(task, task.StageAssignments.ToList()) : x.Value)
        }), JsonOptions);
        plan.ScopeRevision++; plan.Revision++; plan.UpdatedAt = now;
        foreach (var old in plan.Executions.Where(x => x.Status != "Superseded" && x.Status != "Cancelled").ToArray())
        {
            if (old.Status != "WaitingForChildren" || old.Stages.Count != 0)
                throw new InvalidOperationException("Only unstarted aggregate validation can admit a linked corrective Bug.");
            old.Status = "Superseded";
            db.WorkDeliveryExecutions.Add(new() { Id = Guid.NewGuid(), PlanId = plan.Id, Scope = old.Scope, WorkItemId = old.WorkItemId,
                BoardId = old.BoardId, ScopeRevision = plan.ScopeRevision, CreatedAt = now, UpdatedAt = now });
        }
    }
}
