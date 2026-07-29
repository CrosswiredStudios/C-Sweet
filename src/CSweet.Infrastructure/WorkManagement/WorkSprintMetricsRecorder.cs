using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public static class WorkSprintMetricsRecorder
{
    public static async Task RecordAsync(
        CSweetDbContext db,
        Guid? sprintId,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (!sprintId.HasValue) return;
        var sprint = db.WorkSprints.Local.SingleOrDefault(x => x.Id == sprintId.Value)
            ?? await db.WorkSprints.SingleOrDefaultAsync(
                x => x.Id == sprintId.Value, cancellationToken);
        if (sprint is null) return;

        var items = (await db.CoreWorkTasks.Where(x =>
                x.OrganizationId == sprint.OrganizationId &&
                (x.BoardId == sprint.BoardId || x.SprintId == sprint.Id))
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        foreach (var entry in db.ChangeTracker.Entries<WorkTask>().Where(x =>
                     x.State != EntityState.Deleted &&
                     x.Entity.OrganizationId == sprint.OrganizationId))
            items[entry.Entity.Id] = entry.Entity;
        var scope = items.Values.Where(x =>
                x.BoardId == sprint.BoardId && x.SprintId == sprint.Id)
            .ToList();
        var completed = scope.Where(x => x.Status == WorkTaskStatus.Completed).ToList();
        var scopePoints = scope.Sum(x => x.EstimatePoints ?? 0);
        var completedPoints = completed.Sum(x => x.EstimatePoints ?? 0);
        db.WorkSprintMetricPoints.Add(new WorkSprintMetricPoint
        {
            Id = Guid.NewGuid(),
            OrganizationId = sprint.OrganizationId,
            BoardId = sprint.BoardId,
            SprintId = sprint.Id,
            Reason = reason,
            ScopeItemCount = scope.Count,
            CompletedItemCount = completed.Count,
            ScopePoints = scopePoints,
            CompletedPoints = completedPoints,
            RemainingPoints = scopePoints - completedPoints,
            OccurredAt = occurredAt
        });
    }
}
