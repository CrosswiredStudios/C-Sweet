using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public static class WorkSprintReportBuilder
{
    public static async Task<WorkSprintReportResponse> BuildAsync(
        CSweetDbContext db,
        Guid organizationId,
        Guid boardId,
        CancellationToken cancellationToken)
    {
        var snapshots = await db.WorkSprintSnapshots.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == boardId)
            .OrderByDescending(x => x.CompletedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        var averageVelocity = snapshots.Count == 0
            ? 0
            : Math.Round(snapshots.Average(x => x.CompletedPoints), 2);
        var capacitySnapshots = snapshots.Where(x => x.CapacityPoints > 0).ToList();
        decimal? utilization = capacitySnapshots.Count == 0
            ? null
            : Math.Round(capacitySnapshots.Average(x =>
                x.CompletedPoints / x.CapacityPoints!.Value * 100), 2);

        var metrics = await db.WorkSprintMetricPoints.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == boardId)
            .OrderByDescending(x => x.OccurredAt)
            .Take(1000)
            .ToListAsync(cancellationToken);
        metrics.Reverse();
        var metricSprintIds = metrics.Select(x => x.SprintId).Distinct().ToHashSet();
        var sprints = await db.WorkSprints.AsNoTracking()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.BoardId == boardId &&
                (metricSprintIds.Contains(x.Id) ||
                 x.Status == WorkSprintStatus.Active))
            .ToListAsync(cancellationToken);
        var series = sprints
            .Select(sprint => new WorkSprintBurndownSeriesResponse(
                sprint.Id,
                sprint.Name,
                sprint.Status.ToString(),
                sprint.CapacityPoints,
                metrics.Where(x => x.SprintId == sprint.Id)
                    .Select(x => new WorkSprintMetricPointResponse(
                        x.Id, x.OccurredAt, x.Reason,
                        x.ScopeItemCount, x.CompletedItemCount,
                        x.ScopePoints, x.CompletedPoints, x.RemainingPoints))
                    .ToList()))
            .OrderByDescending(x => x.Status == "Active")
            .ThenByDescending(x => x.Points.LastOrDefault()?.OccurredAt)
            .ToList();
        var active = sprints.SingleOrDefault(x => x.Status == WorkSprintStatus.Active);
        WorkSprintForecastResponse? forecast = null;
        if (active is not null)
        {
            var latest = metrics.LastOrDefault(x => x.SprintId == active.Id);
            var currentItems = latest is null
                ? await db.CoreWorkTasks.AsNoTracking()
                    .Where(x => x.SprintId == active.Id)
                    .ToListAsync(cancellationToken)
                : [];
            var remaining = latest?.RemainingPoints ??
                currentItems.Where(x => x.Status != WorkTaskStatus.Completed)
                    .Sum(x => x.EstimatePoints ?? 0);
            var scopePoints = latest?.ScopePoints ??
                currentItems.Sum(x => x.EstimatePoints ?? 0);
            forecast = new WorkSprintForecastResponse(
                active.Id,
                active.Name,
                remaining,
                averageVelocity,
                remaining == 0
                    ? 0
                    : averageVelocity > 0
                        ? Math.Ceiling(remaining / averageVelocity)
                        : null,
                active.CapacityPoints > 0 && scopePoints > active.CapacityPoints);
        }

        return new WorkSprintReportResponse(
            boardId,
            snapshots.Count,
            averageVelocity,
            snapshots.Sum(x => x.CompletedPoints),
            utilization,
            snapshots.Select(WorkSprintSnapshotFactory.ToResponse).ToList(),
            series,
            forecast);
    }
}
