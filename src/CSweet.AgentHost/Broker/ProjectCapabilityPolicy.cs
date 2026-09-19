using CSweet.Agent.SDK;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.AgentHost.Broker;

/// <summary>Manifest-declared prerequisite, independent of any particular agent implementation.</summary>
public sealed class ProjectCapabilityPolicy(CSweetDbContext db, ProjectWorkPolicy policy)
{
    public async Task ValidateAsync(Guid org, Guid installation, string capability, JsonElement input, CancellationToken ct)
    {
        if (!IsDelivery(capability) || !await policy.RequiresProjectAsync(org, installation, ct)) return;
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct)
            ?? throw new UnauthorizedAccessException("The agent employee is unavailable.");
        var itemId = Id(input, "workItemId") ?? Id(input, "itemId") ?? Id(input, "rootItemId") ?? Id(input, "taskItemId");
        if (itemId is null && Id(input, "workspaceId") is { } workspace)
            itemId = await db.SourceControlWorkspaces.Where(x => x.OrganizationId == org && x.Id == workspace).Select(x => (Guid?)x.WorkItemId).SingleOrDefaultAsync(ct);
        if (itemId is { } work)
        {
            var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == work, ct)
                ?? throw new InvalidOperationException("project.work_not_found: The delivery ticket is unavailable.");
            if (item.AssignedAgentInstallationId != installation)
                throw new UnauthorizedAccessException("This delivery ticket is not assigned to this agent.");
            await policy.RequireWorkAsync(item, ct); return;
        }
        if (Id(input, "boardId") is { } board) { await policy.RequireAsync(org, actor.Id, board, ct); return; }
        var project = Id(input, "workstreamId") ?? Id(input, "projectId") ?? Id(input, "productOrWorkstreamId");
        if (project.HasValue)
        {
            var boardId = await db.WorkBoards.Where(x => x.OrganizationId == org && x.WorkstreamId == project && x.ArchivedAt == null)
                .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (boardId.HasValue) { await policy.RequireAsync(org, actor.Id, boardId.Value, ct); return; }
        }
        throw new InvalidOperationException("project.required: This agent requires an active project and explicit assignment before delivery. Retain the request and offer project setup or manager assistance.");
    }
    internal static bool IsDelivery(string capability) =>
        capability is SourceControlCapabilities.ProvisionRepository or PersonalTodoActions.CreatePlan or PersonalTodoActions.ReportPlanTask ||
        capability.StartsWith("git.workspace.", StringComparison.Ordinal) ||
        capability.StartsWith("source-control.personal-work.", StringComparison.Ordinal) ||
        capability is WorkItemActions.Create or WorkItemActions.Start or WorkItemActions.RevisePlanning or WorkItemActions.FinalizeDelivery;
    private static Guid? Id(JsonElement input, string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;
}
