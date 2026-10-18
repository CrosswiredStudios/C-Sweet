using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public static class WorkDeliveryTaskAuthorization
{
    public static async Task PreventManualCompletionAsync(CSweetDbContext db, WorkTask item, CancellationToken ct)
    {
        if (item.TypeKey == WorkItemTypeKeys.WorkflowTicketV1)
            throw new InvalidOperationException("Complete this workflow ticket through its declared steps.");
        var bound = item.DeliverySpecificationJson is { } json &&
            JsonSerializer.Deserialize<WorkItemDeliverySpecification>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.DeliveryPlanId.HasValue == true;
        var board = item.BoardId.HasValue ? await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.BoardId, ct) : null;
        var revised = board?.ProfileKey is WorkBoardProfileKeys.SoftwareDeliveryV2 or WorkBoardProfileKeys.ProjectDeliveryV2 ||
            board is not null && await db.WorkOrchestrationPolicyRevisions.AnyAsync(x => x.BoardId == board.Id && x.IsPublished &&
                x.Stages.Any(s => s.PlatformAction == HierarchicalWorkflows.TaskIntegrationAction), ct);
        if (bound || revised || await db.WorkDeliveryExecutions.AnyAsync(x => x.WorkItemId == item.Id && x.Status != "Superseded" && x.Status != "Cancelled", ct))
            throw new InvalidOperationException("Complete this work through its declared workflow; a direct move to Done cannot replace the recorded execution.");
    }
    public static async Task<(WorkDeliveryPlan Plan, WorkDeliveryBranchBinding? Branch)> RequireAsync(
        CSweetDbContext db, WorkTask item, CancellationToken ct)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>(item.DeliverySpecificationJson ?? "null", json)
            ?? throw new InvalidOperationException("Finalize this task's delivery against its activated plan.");
        if (delivery.DeliveryPlanId is not { } planId)
            throw new InvalidOperationException("Bind this deliverable to its declared delivery plan.");
        var plan = await db.WorkDeliveryPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId && x.OrganizationId == item.OrganizationId && x.Status == "Active", ct)
            ?? throw new InvalidOperationException("The task's delivery plan is not active.");
        var scopes = JsonSerializer.Deserialize<WorkDeliveryScopeSnapshot[]>(plan.ScopesJson, json)!;
        var inputs = scopes.Where(s => s.BoardId == item.BoardId && s.ChildIds.Contains(item.Id)).ToArray();
        if (inputs.Length == 0 || inputs.Any(s => !s.ChildPlanningRevisions.TryGetValue(item.Id, out var revision) || revision != item.PlanningRevision))
            throw new UnauthorizedAccessException("The task's planning is outside the declared evidence inputs.");
        var assignments = await db.WorkItemStageAssignments.AsNoTracking().Where(x => x.WorkItemId == item.Id).ToListAsync(ct);
        if (inputs.Any(s => !s.ChildDeliveryDigests.TryGetValue(item.Id, out var digest) || digest != WorkDeliveryService.TaskDeliveryDigest(item, assignments)))
            throw new InvalidOperationException("Task delivery or staffing changed; pause and amend the plan before executing this task.");
        if (delivery.DeliveryKind == "Artifact")
        {
            if (delivery.RepositoryId != Guid.Empty || !string.IsNullOrEmpty(delivery.BaseBranch))
                throw new ArgumentException("Artifact delivery does not bind a repository branch.");
            return (plan, null);
        }
        var branches = JsonSerializer.Deserialize<WorkDeliveryBranchBinding[]>(plan.BranchesJson, json)!;
        var branch = branches.Where(b => inputs.Any(s => s.Scope == b.Scope && s.ItemId == b.ItemId) && b.RepositoryId == delivery.RepositoryId && b.SourceBranch == delivery.BaseBranch).DistinctBy(b => b.SourceBranch).SingleOrDefault();
        if (delivery.DeliveryKind != "Code" || branch is null || branch.SourceBranch != delivery.BaseBranch)
            throw new UnauthorizedAccessException("The task must implement against its explicitly authorized integration branch.");
        return (plan, branch);
    }
}
