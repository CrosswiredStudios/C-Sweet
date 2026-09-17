using System.Text.Json;
using CSweet.Application.Analytics;
using CSweet.Contracts.Analytics;
using CSweet.Domain.Analytics;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public sealed class WorkEfficiencyService(CSweetDbContext db, TimeProvider clock) : IWorkEfficiencyService
{
    private const int MaximumRows = 50_000;

    public async Task<WorkEfficiencyResponse> GetAsync(Guid organizationId, DateTimeOffset? from = null,
        DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        if (from >= to) throw new ArgumentException("The start must precede the end of the reporting window.");
        var now = clock.GetUtcNow();
        var logs = await db.AgentRunLogs.AsNoTracking().ProviderCalls().Where(x =>
                x.OrganizationId == organizationId && x.InvocationKind != "benchmark-evaluation" &&
                (!from.HasValue || x.StartedAt >= from) && (!to.HasValue || x.StartedAt < to))
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).Take(MaximumRows + 1)
            .Select(x => new AgentRunLog { Id = x.Id, WorkItemId = x.WorkItemId, WorkstreamId = x.WorkstreamId,
                AncestorWorkItemIdsJson = x.AncestorWorkItemIdsJson, AttributionKind = x.AttributionKind,
                MeasurementKind = x.MeasurementKind, ReportedInputTokens = x.ReportedInputTokens,
                ReportedOutputTokens = x.ReportedOutputTokens, TokenInputCount = x.TokenInputCount, TokenOutputCount = x.TokenOutputCount,
                TokenCachedInputCount = x.TokenCachedInputCount, TokenReasoningCount = x.TokenReasoningCount,
                Status = x.Status, StartedAt = x.StartedAt, ProviderStartedAt = x.ProviderStartedAt,
                CompletedAt = x.CompletedAt, DurationMs = x.DurationMs }).ToListAsync(cancellationToken);
        var truncated = logs.Count > MaximumRows;
        if (truncated) logs.RemoveAt(MaximumRows);
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAt).Take(MaximumRows + 1).ToListAsync(cancellationToken);
        var projects = await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAt).Take(MaximumRows + 1).ToListAsync(cancellationToken);
        var boards = await db.WorkBoards.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.Id, x => x.WorkstreamId, cancellationToken);
        var events = await db.WorkLifecycleEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Take(MaximumRows + 1).ToListAsync(cancellationToken);
        truncated |= items.Count > MaximumRows || projects.Count > MaximumRows || events.Count > MaximumRows;
        var direct = logs.Where(x => x.WorkItemId.HasValue).ToLookup(x => x.WorkItemId!.Value);
        var descendants = logs.SelectMany(x => Ancestors(x).Distinct().Select(id => (id, log: x)))
            .ToLookup(x => x.id, x => x.log);
        var histories = events.ToLookup(x => (x.ResourceKind, x.ResourceId));
        var itemRows = items.Take(MaximumRows).Select(item => new WorkEfficiencyRow(item.Id,
            item.ParentWorkTaskId, item.BoardId.HasValue ? boards.GetValueOrDefault(item.BoardId.Value) : null,
            item.Kind.ToString(), item.Title, item.Status.ToString(), Sum(direct[item.Id]),
            Sum(direct[item.Id].Concat(descendants[item.Id]).DistinctBy(x => x.Id)),
            Lifecycle(histories[("WorkItem", item.Id)], item.CreatedAt, now, truncated))).ToList();
        var projectRows = projects.Take(MaximumRows).Select(project => new WorkEfficiencyRow(project.Id, null,
            project.Id, "Project", project.Name, project.Status.ToString(),
            Sum(logs.Where(x => x.WorkstreamId == project.Id && x.WorkItemId == null)),
            Sum(logs.Where(x => x.WorkstreamId == project.Id)),
            Lifecycle(histories[("Project", project.Id)], project.CreatedAt, now, truncated))).ToList();
        return new(organizationId, now, from, to, Sum(logs),
            Sum(logs.Where(x => x.AttributionKind == "BusinessOverhead")),
            Sum(logs.Where(x => x.AttributionKind == "Unknown")), projectRows, itemRows, truncated,
            "Tokens are provider-reported input + output; cached input and reasoning are subsets. " +
            "Legacy calls and missing usage are incomplete coverage. Historical ownership is captured at invocation. " +
            "Lifecycle durations cover the item's lifetime, independently of the usage date filter." +
            (truncated ? " This response is partial; narrow the date range. Do not use it as a complete benchmark total." : ""));
    }

    public async Task<EfficiencyActivityResponse> GetActivityAsync(Guid organizationId, Guid? workItemId,
        Guid? workstreamId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Invalid page; limit must be 1–200.");
        var rows = await db.AgentRunLogs.AsNoTracking().ProviderCalls().Where(x => x.OrganizationId == organizationId &&
                (!workItemId.HasValue || x.WorkItemId == workItemId) && (!workstreamId.HasValue || x.WorkstreamId == workstreamId))
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).Skip(offset).Take(limit + 1)
            .Select(x => new EfficiencyCallResponse(x.Id, x.StartedAt, x.CompletedAt, x.Status, x.Model,
                x.AgentPackageVersion, x.InvocationKind, x.WorkItemId, x.ReportedInputTokens ?? x.TokenInputCount,
                x.ReportedOutputTokens ?? x.TokenOutputCount, x.AttributionKind, x.MeasurementKind))
            .ToListAsync(cancellationToken);
        return new(rows.Take(limit).ToList(), offset, rows.Count > limit);
    }

    public static EfficiencyUsage Sum(IEnumerable<AgentRunLog> source)
    {
        var rows = source.ToList();
        return new(rows.Count, rows.Sum(InferenceMeasurements.Input), rows.Sum(InferenceMeasurements.Output),
            rows.Sum(x => (long)(x.TokenCachedInputCount ?? 0)), rows.Sum(x => (long)(x.TokenReasoningCount ?? 0)),
            rows.Count(InferenceMeasurements.HasInput), rows.Count(InferenceMeasurements.HasOutput),
            rows.Count(x => x.Status == "Failed"), rows.Count(x => x.Status == "Cancelled"),
            rows.Count(x => x.MeasurementKind == "Legacy"), rows.Sum(x => x.ProviderStartedAt.HasValue && x.CompletedAt.HasValue
                ? Math.Max(0, (long)(x.CompletedAt.Value - x.ProviderStartedAt.Value).TotalMilliseconds) : x.DurationMs))
        { FullyReportedCalls = rows.Count(x => InferenceMeasurements.HasInput(x) && InferenceMeasurements.HasOutput(x)) };
    }

    private static IEnumerable<Guid> Ancestors(AgentRunLog row)
    {
        try { return JsonSerializer.Deserialize<Guid[]>(row.AncestorWorkItemIdsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    public static EfficiencyLifecycle Lifecycle(IEnumerable<WorkLifecycleEvent> source,
        DateTimeOffset createdAt, DateTimeOffset now, bool truncated = false)
    {
        var rows = source.OrderBy(x => x.OccurredAt).ToList();
        var start = rows.FirstOrDefault(x => x.Status is "Running" or "Active")?.OccurredAt;
        var last = rows.LastOrDefault();
        var completed = last?.Status == "Completed" ? last.OccurredAt : (DateTimeOffset?)null;
        long waiting = 0;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Status is "Blocked" or "WaitingForApproval" or "WaitingForHuman")
                waiting += Math.Max(0, (long)((i + 1 < rows.Count ? rows[i + 1].OccurredAt : now) - rows[i].OccurredAt).TotalMilliseconds);
        var complete = !truncated && rows.Count > 0 && rows[0].PreviousStatus is null && rows[0].OccurredAt == createdAt;
        return new(createdAt, start, completed,
            completed.HasValue ? Math.Max(0, (long)(completed.Value - createdAt).TotalMilliseconds) : null,
            completed.HasValue && start.HasValue ? Math.Max(0, (long)(completed.Value - start.Value).TotalMilliseconds) : null,
            waiting, rows.Count(x => x.PreviousStatus == "Completed" && x.Status != "Completed"), complete);
    }
}
