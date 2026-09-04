using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Builds flow measurements exclusively from platform-owned execution records.</summary>
public static class WorkFlowMetricsBuilder
{
    public static async Task<Wire.WorkFlowMetricsReport> BuildAsync(
        CSweetDbContext db,
        Guid organizationId,
        Wire.ReadWorkFlowMetricsRequest request,
        CancellationToken token)
    {
        var end = request.WindowEnd ?? DateTimeOffset.UtcNow;
        var start = request.WindowStart ?? end.AddDays(-28);
        if (start >= end || end - start > TimeSpan.FromDays(90))
            throw new ArgumentException("The metrics window must be positive and no longer than 90 days.");
        if (request.CompletedSprintLimit is < 1 or > 20)
            throw new ArgumentException("CompletedSprintLimit must be between 1 and 20.");

        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.BoardId && x.OrganizationId == organizationId, token)
            ?? throw new KeyNotFoundException("Board was not found.");
        if (request.TeamId.HasValue && board.TeamId != request.TeamId)
            throw new UnauthorizedAccessException("The requested team does not own this board.");
        if (request.WorkstreamId.HasValue && board.WorkstreamId != request.WorkstreamId)
            throw new UnauthorizedAccessException("The requested workstream does not own this board.");

        var snapshots = await db.WorkSprintSnapshots.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == request.BoardId &&
                        x.CompletedAt >= start && x.CompletedAt <= end)
            .OrderByDescending(x => x.CompletedAt)
            .Take(request.CompletedSprintLimit)
            .ToListAsync(token);
        var stages = await db.WorkStageExecutions.AsNoTracking()
            .Include(x => x.Attempts)
            .Include(x => x.ItemExecution)!.ThenInclude(x => x!.SprintExecution)
            .Where(x => x.ItemExecution!.SprintExecution!.OrganizationId == organizationId &&
                        x.ItemExecution.SprintExecution.BoardId == request.BoardId &&
                        x.CreatedAt <= end && (x.CompletedAt == null || x.CompletedAt >= start))
            .ToListAsync(token);
        var itemExecutions = await db.WorkItemExecutions.AsNoTracking()
            .Include(x => x.SprintExecution)
            .Where(x => x.SprintExecution!.OrganizationId == organizationId &&
                        x.SprintExecution.BoardId == request.BoardId &&
                        x.CreatedAt <= end && (x.CompletedAt == null || x.CompletedAt >= start))
            .ToListAsync(token);
        var items = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == request.BoardId)
            .ToListAsync(token);
        var metricPoints = await db.WorkSprintMetricPoints.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == request.BoardId &&
                        x.OccurredAt >= start && x.OccurredAt <= end)
            .ToListAsync(token);

        var installationIds = stages.Where(x => x.AgentInstallationId.HasValue)
            .Select(x => x.AgentInstallationId!.Value).Distinct().ToList();
        var manifests = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
            .Where(x => installationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.PackageVersion!.ManifestJson, token);

        var days = Math.Max((decimal)(end - start).TotalDays, 1m);
        var completedStages = stages.Where(x => x.Status == WorkStageExecutionStatus.Completed &&
                                                 x.CompletedAt >= start && x.CompletedAt <= end).ToList();
        var stageDurations = completedStages.Select(DurationHours).ToList();
        var retryCount = stages.Sum(x => Math.Max(0, x.Attempts.Count - 1));
        var blocked = stages.Where(x => x.Status is WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Backoff).ToList();
        var capacitySnapshots = snapshots.Where(x => x.CapacityPoints > 0).ToList();
        var averageVelocity = snapshots.Count == 0 ? 0 : Math.Round(snapshots.Average(x => x.CompletedPoints), 2);
        var utilization = capacitySnapshots.Count == 0 ? 0 : Math.Round(capacitySnapshots.Average(x =>
            x.CompletedPoints / x.CapacityPoints!.Value * 100), 2);
        var remaining = items.Where(x => x.Status is not WorkTaskStatus.Completed and not WorkTaskStatus.Cancelled)
            .Sum(x => x.EstimatePoints ?? 0);
        var activeSprint = await db.WorkSprints.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.BoardId == request.BoardId && x.Status == WorkSprintStatus.Active, token);
        var activeScope = activeSprint is null ? 0 : items.Where(x => x.SprintId == activeSprint.Id).Sum(x => x.EstimatePoints ?? 0);

        var teamConditions = new List<string>();
        if (snapshots.Count < 2) teamConditions.Add(Wire.WorkFlowMetricConditionCodes.InsufficientCompletedSprints);
        if (completedStages.Count < 10) teamConditions.Add(Wire.WorkFlowMetricConditionCodes.InsufficientAttributedStages);
        if (snapshots.Count == 0 || completedStages.Count == 0) teamConditions.Add(Wire.WorkFlowMetricConditionCodes.SparseHistoricalBaseline);

        var principals = stages.Where(x => x.OrganizationUserId.HasValue)
            .GroupBy(x => new { UserId = x.OrganizationUserId!.Value, x.AgentInstallationId })
            .Select(group =>
            {
                var completed = group.Where(x => x.Status == WorkStageExecutionStatus.Completed &&
                                                  x.CompletedAt >= start && x.CompletedAt <= end).ToList();
                var durations = completed.Select(DurationHours).ToList();
                var currentBlocked = group.Where(x => x.Status is WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Backoff).ToList();
                var retries = group.Sum(x => Math.Max(0, x.Attempts.Count - 1));
                var conditions = new List<string>();
                if (completed.Count < 10) conditions.Add(Wire.WorkFlowMetricConditionCodes.InsufficientAttributedStages);
                if (snapshots.Count < 2) conditions.Add(Wire.WorkFlowMetricConditionCodes.InsufficientCompletedSprints);
                return new Wire.WorkFlowPrincipalMetrics(
                    group.Key.UserId, group.Key.AgentInstallationId,
                    group.Key.AgentInstallationId.HasValue && manifests.TryGetValue(group.Key.AgentInstallationId.Value, out var manifest)
                        ? ReadPrimaryRole(manifest) : null,
                    group.Count(), completed.Count, Math.Round(completed.Count / days * 7, 2),
                    Percentile(durations, 0.5), Percentile(durations, 0.85),
                    group.Count(x => x.Status is WorkStageExecutionStatus.Running or WorkStageExecutionStatus.Dispatching),
                    group.Count(x => x.Status == WorkStageExecutionStatus.Pending), currentBlocked.Count,
                    Math.Round(currentBlocked.Sum(x => (decimal)(end - x.UpdatedAt).TotalHours), 2), retries,
                    group.Any() ? Math.Round((decimal)retries / group.Count() * 100, 2) : 0,
                    conditions);
            })
            .OrderBy(x => x.RoleKey).ThenBy(x => x.OrganizationUserId)
            .ToList();

        var team = new Wire.WorkFlowTeamMetrics(
            snapshots.Count, averageVelocity, utilization,
            itemExecutions.Count(x => x.Status == WorkItemExecutionStatus.Completed && x.CompletedAt >= start && x.CompletedAt <= end),
            completedStages.Count, Math.Round(completedStages.Count / days * 7, 2),
            Percentile(stageDurations, 0.5), Percentile(stageDurations, 0.85),
            items.Count(x => x.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForApproval),
            items.Count(x => x.Status is WorkTaskStatus.Backlog or WorkTaskStatus.Ready or WorkTaskStatus.Assigned),
            blocked.Count, Math.Round(blocked.Sum(x => (decimal)(end - x.UpdatedAt).TotalHours), 2),
            metricPoints.Count(x => x.Reason.Contains("carried-over", StringComparison.OrdinalIgnoreCase)),
            metricPoints.Count(x => x.Reason.Contains("scope", StringComparison.OrdinalIgnoreCase)), retryCount,
            stages.Count == 0 ? 0 : Math.Round((decimal)retryCount / stages.Count * 100, 2),
            remaining, averageVelocity, remaining == 0 ? 0 : averageVelocity > 0 ? Math.Ceiling(remaining / averageVelocity) : 0,
            activeSprint?.CapacityPoints > 0 && activeScope > activeSprint.CapacityPoints);
        var source = $"{board.Revision}|{snapshots.Count}|{stages.Count}|{metricPoints.Count}|{stages.MaxBy(x => x.UpdatedAt)?.UpdatedAt:O}";
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return new Wire.WorkFlowMetricsReport(request.BoardId, board.TeamId, board.WorkstreamId,
            start, end, request.CompletedSprintLimit, revision, DateTimeOffset.UtcNow, team, principals,
            teamConditions.Distinct(StringComparer.Ordinal).ToList());
    }

    private static decimal DurationHours(WorkStageExecution stage) =>
        Math.Round((decimal)((stage.CompletedAt ?? stage.UpdatedAt) - stage.CreatedAt).TotalHours, 2);

    private static decimal Percentile(IReadOnlyList<decimal> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var ordered = values.OrderBy(x => x).ToList();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static string? ReadPrimaryRole(string? manifestJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestJson)) return null;
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("rolePolicy", out var policy) ||
                !policy.TryGetProperty("declaredRoleKeys", out var roles) || roles.ValueKind != JsonValueKind.Array)
                return null;
            return roles.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.String).GetString();
        }
        catch (JsonException) { return null; }
    }
}
