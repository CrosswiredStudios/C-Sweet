using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private sealed record IncidentWorkItemDestination(string Link, string Label);

    // Resolve exact persisted identities against current board membership. Never guess an
    // affected ticket from evidence text, an employee's latest project, or a board name.
    private async Task<IncidentWorkItemDestination?> ResolveWorkItemDestinationAsync(
        ProjectIncident incident, CancellationToken token)
    {
        // A chat/collaboration failure belongs to that conversation. Its affected item can
        // identify attribution scope without being the operation that needs investigation.
        if (IncidentSource(incident.Fingerprint, "chat") is not null ||
            IncidentSource(incident.Fingerprint, "coordination") is not null) return null;
        var itemId = incident.AffectedWorkItemId ?? IncidentSource(incident.Fingerprint, "ticket") ??
            IncidentSource(incident.Fingerprint, "dependency-wait");
        if (itemId is null && IncidentSource(incident.Fingerprint, "stage") is { } stageId)
            itemId = await db.WorkStageExecutions.AsNoTracking()
                .Where(x => x.Id == stageId && x.ItemExecution != null && x.ItemExecution.WorkItem != null &&
                    x.ItemExecution.WorkItem.OrganizationId == incident.OrganizationId)
                .Select(x => (Guid?)x.ItemExecution!.WorkItemId).SingleOrDefaultAsync(token);
        if (itemId is null) return null;

        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == itemId && x.OrganizationId == incident.OrganizationId && x.ArchivedAt == null, token);
        if (item?.BoardId is not { } boardId) return null;
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == boardId && x.OrganizationId == incident.OrganizationId && x.ArchivedAt == null, token);
        if (board is null) return null;
        var projectId = board.WorkstreamId ?? CSweetDbContext.ReadGuidProperty(item.PersonalWorkContextJson, "workstreamId");
        if (projectId != incident.WorkstreamId) return null;

        var label = incident.Status == "Open" && item.Status is WorkTaskStatus.Blocked or WorkTaskStatus.Failed
            ? "Open blocked item" : "Open affected item";
        if (board.Kind == WorkBoardKind.Personal)
        {
            if (board.OwnerOrganizationUserId is not { } owner || !await db.CoreOrganizationUsers.AsNoTracking()
                .AnyAsync(x => x.Id == owner && x.OrganizationId == incident.OrganizationId && x.IsActive, token))
                return null;
            return new($"/organizations/{incident.OrganizationId}/employees/{owner}?tab=personal-board&item={item.Id}", label);
        }
        return new($"/organizations/{incident.OrganizationId}/projects/boards/{board.Id}?item={item.Id}", label);
    }
}
