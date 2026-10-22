using CSweet.Agent.SDK;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    internal static string DependencyOwnerSummary(string blocker, string dependencies)
    {
        var dependencyWords = dependencies.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var blockerWords = blocker.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var budget = Math.Max(0, 80 - dependencyWords.Length);
        return string.Join(" ", blockerWords.Take(budget)) + (blockerWords.Length > budget ? "…" : "") + " " + dependencies;
    }

    private async Task<ProjectDiagnosticEvidence?> DeliveryDependencyStatusAsync(ProjectIncident incident, CancellationToken token)
    {
        var boardIds = await db.WorkBoards.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
            x.WorkstreamId == incident.WorkstreamId && x.Kind == CSweet.Domain.WorkManagement.WorkBoardKind.Standard)
            .Select(x => x.Id).ToArrayAsync(token);
        var sprintIds = await db.WorkSprints.AsNoTracking().Where(x => boardIds.Contains(x.BoardId) &&
            (x.Status == CSweet.Domain.WorkManagement.WorkSprintStatus.Active || x.Status == CSweet.Domain.WorkManagement.WorkSprintStatus.Paused))
            .Select(x => x.Id).ToArrayAsync(token);
        if (sprintIds.Length == 0) return null;
        var rows = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Dependencies).Where(x =>
            x.OrganizationId == incident.OrganizationId && x.BoardId != null && boardIds.Contains(x.BoardId.Value)).ToArrayAsync(token);
        if (rows.Length == 0) return null;
        var scope = rows.Where(x => x.IsExecutable && x.SprintId != null && sprintIds.Contains(x.SprintId.Value) &&
            x.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)).ToArray();
        var states = rows.ToDictionary(x => x.Id, x => new WorkDependencyState(x.Id, x.Identifier ?? x.Title,
            x.Status.ToString(), x.Dependencies.Select(d => d.DependsOnWorkItemId).ToArray()));
        var waiting = scope.Where(x => x.Status is WorkTaskStatus.Ready or WorkTaskStatus.Backlog &&
            WorkDependencyStatus.UnmetRoots(x.Id, states).Count > 0).ToArray();
        var running = scope.Count(x => x.Status == WorkTaskStatus.Running);
        var ready = scope.Count(x => x.Status == WorkTaskStatus.Ready && WorkDependencyStatus.UnmetRoots(x.Id, states).Count == 0);
        var blocked = scope.Count(x => x.Status == WorkTaskStatus.Blocked);
        var affected = waiting.Count(x => WorkDependencyStatus.UnmetRoots(x.Id, states).Any(d => d.Id == incident.AffectedWorkItemId));
        var summary = $"Active sprint tasks: {running} running; {ready} Ready with completed prerequisites; {waiting.Length} waiting for prerequisites; {blocked} blocked.";
        if (affected > 0) summary += $" {affected} task(s) depend directly or indirectly on the affected item.";
        if (running == 0 && ready == 0) summary += " No sprint task is currently running; none of the Ready tasks has completed prerequisites.";
        return new("CurrentDeliveryDependencyStatus", incident.WorkstreamId, rows.Max(x => x.UpdatedAt), summary);
    }
}
