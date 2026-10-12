using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthReader
{
    private async Task ObserveDeliveryPlansAsync(Workstream project, Guid[] boards, List<HealthFailure> failures,
        HashSet<string> recovered, List<ProjectDiagnosticEvidence> evidence, CancellationToken token)
    {
        var plans = await db.WorkDeliveryPlans.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            x.WorkstreamId == project.Id).ToListAsync(token);
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            x.BoardId.HasValue && boards.Contains(x.BoardId.Value) && x.ArchivedAt == null).ToListAsync(token);
        foreach (var plan in plans)
        {
            var key = $"delivery-plan:{plan.Id:N}";
            if (plan.Status is "Completed" or "Cancelled") { recovered.Add(key); continue; }
            if (plan.Status is not ("Draft" or "Active" or "Paused")) continue;
            var scopes = JsonSerializer.Deserialize<WorkDeliveryScopeSnapshot[]>(plan.ScopesJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var details = new List<string>();
            if (scopes.Length == 0) details.Add("Draft delivery has no approved aggregate scopes.");
            foreach (var scope in scopes.Where(x => x.ItemId.HasValue))
            {
                var current = items.SingleOrDefault(x => x.Id == scope.ItemId && x.BoardId == scope.BoardId);
                if (current is null || current.PlanningRevision != scope.PlanningRevision)
                    details.Add($"{scope.Scope} {scope.ItemId}: pinned planning revision {scope.PlanningRevision}, current {current?.PlanningRevision.ToString() ?? "missing"}.");
                var children = items.Where(x => x.ParentWorkTaskId == scope.ItemId && x.BoardId == scope.BoardId).ToArray();
                if (!scope.ChildIds.Order().SequenceEqual(children.Select(x => x.Id).Order()))
                    details.Add($"{scope.Scope} {scope.ItemId}: approved child membership changed.");
                foreach (var child in children.Where(x => x.Status == CSweet.Domain.Core.WorkTaskStatus.Cancelled))
                    details.Add($"{child.Identifier} ({child.Id}): required child was cancelled; explicit scope amendment is needed.");
                foreach (var child in children.Where(x => !scope.ChildPlanningRevisions.TryGetValue(x.Id, out var revision) || revision != x.PlanningRevision))
                    details.Add($"{child.Identifier} ({child.Id}): pinned planning revision {scope.ChildPlanningRevisions.GetValueOrDefault(child.Id)}, current {child.PlanningRevision}.");
            }
            if (details.Count == 0) { recovered.Add(key); continue; }
            var fact = new ProjectDiagnosticEvidence("WorkDeliveryPlan", plan.Id, plan.UpdatedAt,
                Safe("Draft delivery cannot activate because accepted scope changed. Reconcile the existing draft, refresh affected task deliveries and retry authoritative sprint preflight.\n" + string.Join("\n", details.Take(50))),
                $"/organizations/{project.OrganizationId}/work/boards/{scopes.FirstOrDefault()?.BoardId ?? boards.FirstOrDefault()}");
            if (plan.Status != "Draft") continue; // Never silently amend active work.
            failures.Add(new(key, "Delivery preparation stalled on outdated planning revisions", null, [fact])); evidence.Add(fact);
        }
    }
}
