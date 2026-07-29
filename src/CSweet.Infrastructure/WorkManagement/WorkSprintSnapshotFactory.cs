using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public static class WorkSprintSnapshotFactory
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<WorkSprintSnapshot> EnsureAsync(
        CSweetDbContext db,
        WorkSprint sprint,
        CancellationToken cancellationToken)
    {
        var existing = await db.WorkSprintSnapshots.SingleOrDefaultAsync(
            x => x.SprintId == sprint.Id, cancellationToken);
        if (existing is not null) return existing;
        if (sprint.Status != WorkSprintStatus.Completed || !sprint.CompletedAt.HasValue)
            throw new InvalidOperationException(
                "A completion snapshot requires a completed sprint.");

        var items = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.SprintId == sprint.Id)
            .OrderBy(x => x.BoardRank)
            .Select(x => new WorkSprintSnapshotItemResponse(
                x.Id,
                x.Kind.ToString(),
                x.Title,
                (int)x.Status,
                x.EstimatePoints,
                x.Status == WorkTaskStatus.Completed))
            .ToListAsync(cancellationToken);
        var snapshot = new WorkSprintSnapshot
        {
            Id = Guid.NewGuid(),
            OrganizationId = sprint.OrganizationId,
            BoardId = sprint.BoardId,
            SprintId = sprint.Id,
            SprintName = sprint.Name,
            Goal = sprint.Goal,
            StartedAt = sprint.StartedAt,
            CompletedAt = sprint.CompletedAt.Value,
            CapacityPoints = sprint.CapacityPoints,
            CommittedItemCount = items.Count,
            CompletedItemCount = items.Count(x => x.Completed),
            CommittedPoints = items.Sum(x => x.EstimatePoints ?? 0),
            CompletedPoints = items.Where(x => x.Completed)
                .Sum(x => x.EstimatePoints ?? 0),
            ScopeJson = JsonSerializer.Serialize(items, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WorkSprintSnapshots.Add(snapshot);
        return snapshot;
    }

    public static WorkSprintSnapshotResponse ToResponse(WorkSprintSnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.SprintId,
            snapshot.SprintName,
            snapshot.Goal,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.CapacityPoints,
            snapshot.CommittedItemCount,
            snapshot.CompletedItemCount,
            snapshot.CommittedPoints,
            snapshot.CompletedPoints,
            JsonSerializer.Deserialize<IReadOnlyList<WorkSprintSnapshotItemResponse>>(
                snapshot.ScopeJson, JsonOptions) ?? []);
}
