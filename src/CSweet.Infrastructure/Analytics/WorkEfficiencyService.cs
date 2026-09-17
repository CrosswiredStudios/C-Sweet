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
            .Select(x => new AgentRunLog { Id = x.Id, AgentWorkItemId = x.AgentWorkItemId, AgentWorkAttemptId = x.AgentWorkAttemptId, WorkItemId = x.WorkItemId, WorkstreamId = x.WorkstreamId,
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
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.SourceRevision).ThenBy(x => x.Id).Take(MaximumRows + 1).ToListAsync(cancellationToken);
        truncated |= items.Count > MaximumRows || projects.Count > MaximumRows || events.Count > MaximumRows;
        var intervals = await db.WorkExecutionIntervals.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.StartedAt).Take(MaximumRows + 1).ToListAsync(cancellationToken);
        truncated |= intervals.Count > MaximumRows;
        intervals = intervals.Take(MaximumRows).ToList();
        var openAttempts = intervals.Where(x => x.EndedAt == null).Select(x => x.AgentWorkAttemptId).Distinct().ToArray();
        var expiredAttempts = await db.AgentWorkAttempts.AsNoTracking().Where(x => openAttempts.Contains(x.Id) &&
            x.LeaseExpiresAt <= now).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var interval in intervals.Where(x => x.EndedAt == null && expiredAttempts.Contains(x.AgentWorkAttemptId)))
        { interval.EndedAt = interval.ConfirmedThrough; interval.EndReason = "LeaseLost"; }
        // Historical claim receipts are trustworthy starts, but never evidence of completion
        // or continuous effort. Keep the provenance and incomplete coverage visible.
        var claims = await db.WorkItemActivities.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.EventType == "agent.ticket.claimed").OrderBy(x => x.OccurredAt).Take(MaximumRows + 1).ToListAsync(cancellationToken);
        truncated |= claims.Count > MaximumRows;
        var recordedStarts = events.Where(x => x.Status == "Running").Select(x => (x.ResourceId, x.OccurredAt)).ToHashSet();
        foreach (var claim in claims.Take(MaximumRows))
            if (recordedStarts.Add((claim.WorkItemId, claim.OccurredAt)))
                events.Add(new WorkLifecycleEvent { Id = claim.Id, OrganizationId = organizationId, ResourceId = claim.WorkItemId,
                    Status = "Running", OccurredAt = claim.OccurredAt, Provenance = "HistoricalClaim" });
        var starts = events.Where(x => x.Status == "Running").GroupBy(x => x.ResourceId).ToDictionary(x => x.Key, x => x.Min(e => e.OccurredAt));
        foreach (var interval in intervals.Where(x => x.WorkItemId.HasValue).GroupBy(x => x.WorkItemId).Select(x => x.MinBy(i => i.StartedAt)!))
            if (!starts.TryGetValue(interval.WorkItemId!.Value, out var start) || start > interval.StartedAt)
                events.Add(new WorkLifecycleEvent { Id = interval.Id, OrganizationId = organizationId, ResourceId = interval.WorkItemId!.Value,
                    Status = "Running", OccurredAt = interval.StartedAt, Provenance = "ExecutionInterval" });
        var direct = logs.Where(x => x.WorkItemId.HasValue).ToLookup(x => x.WorkItemId!.Value);
        var descendants = logs.SelectMany(x => Ancestors(x).Distinct().Select(id => (id, log: x)))
            .ToLookup(x => x.id, x => x.log);
        var directIntervals = intervals.Where(x => x.WorkItemId.HasValue).ToLookup(x => x.WorkItemId!.Value);
        var descendantIntervals = intervals.SelectMany(x => Ids(x.AncestorWorkItemIdsJson).Distinct().Select(id => (id, interval: x)))
            .ToLookup(x => x.id, x => x.interval);
        var projectIntervals = intervals.ToLookup(x => x.WorkstreamId);
        var histories = events.ToLookup(x => (x.ResourceKind, x.ResourceId));
        var itemMap = items.ToDictionary(x => x.Id);
        var descendantsByItem = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var item in items)
        {
            var parent = item.ParentWorkTaskId; var seen = new HashSet<Guid> { item.Id };
            while (parent is { } id && seen.Add(id) && itemMap.TryGetValue(id, out var ancestor))
            {
                if (!descendantsByItem.TryGetValue(id, out var children)) descendantsByItem[id] = children = [];
                children.Add(item.Id); parent = ancestor.ParentWorkTaskId;
            }
        }
        var itemRows = items.Take(MaximumRows).Select(item =>
        {
            var own = direct[item.Id].ToList();
            var all = own.Concat(descendants[item.Id]).DistinctBy(x => x.Id).ToList();
            var effort = directIntervals[item.Id].Concat(descendantIntervals[item.Id]).DistinctBy(x => x.Id).ToList();
            var childIds = descendantsByItem.GetValueOrDefault(item.Id) ?? [];
            var history = histories[("WorkItem", item.Id)];
            var children = childIds.SelectMany(id => histories[("WorkItem", id)]);
            var lifecycle = RollupLifecycle(history, children, item.CreatedAt, item.Status.ToString(), now, truncated);
            return new WorkEfficiencyRow(item.Id, item.ParentWorkTaskId,
                item.BoardId.HasValue ? boards.GetValueOrDefault(item.BoardId.Value) : null,
                item.Kind.ToString(), item.Title, item.Status.ToString(),
                Sum(own) with { ActiveAgentTimeMs = Effort(effort.Where(x => x.WorkItemId == item.Id), from, to, now) },
                Sum(all) with { ActiveAgentTimeMs = Effort(effort, from, to, now) }, lifecycle)
            { TimingCoverage = TimingCoverage(lifecycle, effort, all, truncated || HasHistoricalExecution(history.Concat(children))), AttributionCoverage = AttributionCoverage(all, truncated) };
        }).ToList();
        var projectRows = projects.Take(MaximumRows).Select(project =>
        {
            var all = logs.Where(x => x.WorkstreamId == project.Id).ToList();
            var effort = projectIntervals[project.Id].ToList();
            var childIds = items.Where(x => x.BoardId.HasValue && boards.GetValueOrDefault(x.BoardId.Value) == project.Id).Select(x => x.Id);
            var history = histories[("Project", project.Id)];
            var children = childIds.SelectMany(id => histories[("WorkItem", id)]).ToList();
            var lifecycle = RollupLifecycle(history, children, project.CreatedAt, project.Status.ToString(), now, truncated);
            return new WorkEfficiencyRow(project.Id, null, project.Id, "Project", project.Name, project.Status.ToString(),
                Sum(all.Where(x => x.WorkItemId == null)) with { ActiveAgentTimeMs = Effort(effort.Where(x => x.WorkItemId == null), from, to, now) },
                Sum(all) with { ActiveAgentTimeMs = Effort(effort, from, to, now) }, lifecycle)
            { TimingCoverage = TimingCoverage(lifecycle, effort, all, truncated || HasHistoricalExecution(history.Concat(children))), AttributionCoverage = AttributionCoverage(all, truncated) };
        }).ToList();
        return new(organizationId, now, from, to, Sum(logs) with { ActiveAgentTimeMs = Effort(intervals, from, to, now) },
            Sum(logs.Where(x => x.AttributionKind == "BusinessOverhead")),
            Sum(logs.Where(x => x.AttributionKind == "Unknown")) with { ActiveAgentTimeMs = Effort(intervals.Where(x => x.WorkItemId == null), from, to, now) }, projectRows, itemRows, truncated,
            "Tokens are provider-reported input + output; cached input and reasoning are subsets. " +
            "Legacy calls and missing usage are incomplete coverage. Historical ownership is captured at invocation. " +
            "Elapsed and lead times cover the lifetime; tokens and confirmed active agent effort follow the date filter. " +
            "Parallel attempts add effort; model/tool durations are already included. Active effort advances on lease confirmation. " +
            "Historical claim starts are recovered conservatively; missing effort is never estimated." +
            (truncated ? " This response is partial; narrow the date range. Do not use it as a complete benchmark total." : ""));
    }

    public async Task<EfficiencyActivityResponse> GetActivityAsync(Guid organizationId, Guid? workItemId,
        Guid? workstreamId, int offset, int limit, CancellationToken cancellationToken = default, bool? subtree = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        if (from >= to) throw new ArgumentException("The start must precede the end of the reporting window.");
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Invalid page; limit must be 1–200.");
        var rows = await db.AgentRunLogs.AsNoTracking().ProviderCalls().Where(x => x.OrganizationId == organizationId &&
                x.InvocationKind != "benchmark-evaluation" &&
                (!from.HasValue || x.StartedAt >= from) && (!to.HasValue || x.StartedAt < to) &&
                (!workItemId.HasValue || x.WorkItemId == workItemId || subtree == true &&
                    x.AncestorWorkItemIdsJson.Contains(workItemId.Value.ToString())) &&
                (!workstreamId.HasValue || x.WorkstreamId == workstreamId && (subtree != false || x.WorkItemId == null)))
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

    private static string AttributionCoverage(IReadOnlyList<AgentRunLog> logs, bool truncated) =>
        truncated || logs.Any(x => x.AttributionKind == "Unknown" || x.MeasurementKind == "Legacy" ||
            x.AgentWorkItemId.HasValue && !x.AgentWorkAttemptId.HasValue) ? "History incomplete" : "Captured";

    private static bool HasHistoricalExecution(IEnumerable<WorkLifecycleEvent> events) =>
        events.Any(x => x.Status is "Running" or "Active" && x.Provenance != "ExecutionTiming");

    private static string TimingCoverage(EfficiencyLifecycle lifecycle, IReadOnlyList<WorkExecutionInterval> intervals,
        IReadOnlyList<AgentRunLog> logs, bool truncated)
    {
        var attempts = intervals.Select(x => x.AgentWorkAttemptId).ToHashSet();
        return truncated || !lifecycle.CompleteHistory || intervals.Any(x => x.EndReason == "LeaseLost") ||
            logs.Any(x => !x.AgentWorkAttemptId.HasValue || !attempts.Contains(x.AgentWorkAttemptId.Value)) ||
            lifecycle.StartedAt.HasValue && !intervals.Any()
                ? "History incomplete" : "Confirmed";
    }

    public static EfficiencyLifecycle RollupLifecycle(IEnumerable<WorkLifecycleEvent> own,
        IEnumerable<WorkLifecycleEvent> descendants, DateTimeOffset created, string status, DateTimeOffset now, bool truncated = false)
    {
        var childEvents = descendants.ToList();
        var result = Lifecycle(own, created, now, truncated);
        var start = childEvents.Where(x => x.Status is "Running" or "Active").Select(x => (DateTimeOffset?)x.OccurredAt)
            .Append(result.StartedAt).Where(x => x.HasValue).Min();
        var stopped = result.StoppedAt;
        if (stopped.HasValue)
            stopped = childEvents.Where(x => x.Status is "Completed" or "Cancelled" or "Failed")
                .Select(x => (DateTimeOffset?)x.OccurredAt).Append(stopped).Max();
        var terminal = status is "Completed" or "Cancelled" or "Failed";
        var complete = result.CompleteHistory && childEvents.All(x => x.Provenance is "StatusTransition" or "ExecutionTiming") &&
            childEvents.GroupBy(x => x.ResourceId).All(g => g.OrderBy(x => x.OccurredAt).First().PreviousStatus is null);
        return result with
        {
            StartedAt = start, StoppedAt = stopped,
            CompletedAt = status == "Completed" ? stopped : null,
            LeadTimeMs = status == "Completed" && stopped.HasValue ? Math.Max(0, (long)(stopped.Value - created).TotalMilliseconds) : null,
            IsOpen = start.HasValue && !terminal,
            ElapsedTimeMs = start.HasValue && (!terminal || stopped.HasValue)
                ? Math.Max(0, (long)((stopped ?? now) - start.Value).TotalMilliseconds) : null,
            CycleTimeMs = status == "Completed" && stopped.HasValue && start.HasValue
                ? Math.Max(0, (long)(stopped.Value - start.Value).TotalMilliseconds) : null,
            CompleteHistory = complete,
            TimingState = !start.HasValue ? complete && !terminal ? "Not started" : "History incomplete"
                : terminal ? stopped.HasValue ? status == "Completed" ? "Final" : "Stopped" : "History incomplete" : "Running"
        };
    }

    /// <summary>Union intervals within an attempt; sum independent attempts. Clip before union.</summary>
    public static long Effort(IEnumerable<WorkExecutionInterval> source, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        long total = 0;
        foreach (var attempt in source.DistinctBy(x => x.Id).GroupBy(x => x.AgentWorkAttemptId))
        {
            DateTimeOffset? start = null, end = null;
            foreach (var row in attempt.OrderBy(x => x.StartedAt))
            {
                var a = from.HasValue && from > row.StartedAt ? from.Value : row.StartedAt;
                var b = new[] { row.ConfirmedThrough, row.EndedAt ?? now, to ?? now, now }.Min();
                if (b <= a) continue;
                if (end.HasValue && a > end)
                { total += (long)(end.Value - start!.Value).TotalMilliseconds; start = null; }
                start ??= a;
                end = !end.HasValue || b > end ? b : end;
            }
            if (start.HasValue && end.HasValue) total += (long)(end.Value - start.Value).TotalMilliseconds;
        }
        return total;
    }

    private static IEnumerable<Guid> Ids(string json)
    {
        try { return JsonSerializer.Deserialize<Guid[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IEnumerable<Guid> Ancestors(AgentRunLog row)
    {
        try { return JsonSerializer.Deserialize<Guid[]>(row.AncestorWorkItemIdsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    public static EfficiencyLifecycle Lifecycle(IEnumerable<WorkLifecycleEvent> source,
        DateTimeOffset createdAt, DateTimeOffset now, bool truncated = false)
    {
        var rows = source.OrderBy(x => x.OccurredAt).ThenBy(x => x.SourceRevision).ToList();
        var start = rows.FirstOrDefault(x => x.Status is "Running" or "Active")?.OccurredAt;
        var last = rows.LastOrDefault();
        var stopped = last?.Status is "Completed" or "Cancelled" or "Failed" ? last.OccurredAt : (DateTimeOffset?)null;
        var completed = last?.Status == "Completed" ? last.OccurredAt : (DateTimeOffset?)null;
        long waiting = 0;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Status is "Blocked" or "WaitingForApproval" or "WaitingForHuman")
                waiting += Math.Max(0, (long)((i + 1 < rows.Count ? rows[i + 1].OccurredAt : now) - rows[i].OccurredAt).TotalMilliseconds);
        var complete = !truncated && rows.Count > 0 && rows[0].PreviousStatus is null && rows[0].OccurredAt == createdAt && rows.All(x => x.Provenance is "StatusTransition" or "ExecutionTiming");
        return new(createdAt, start, completed,
            completed.HasValue ? Math.Max(0, (long)(completed.Value - createdAt).TotalMilliseconds) : null,
            completed.HasValue && start.HasValue ? Math.Max(0, (long)(completed.Value - start.Value).TotalMilliseconds) : null,
            waiting, rows.Count(x => x.PreviousStatus == "Completed" && x.Status != "Completed"), complete)
        { ElapsedTimeMs = start.HasValue ? Math.Max(0, (long)((stopped ?? now) - start.Value).TotalMilliseconds) : null,
            StoppedAt = stopped, IsOpen = start.HasValue && stopped is null,
            TimingState = start.HasValue ? stopped.HasValue ? completed.HasValue ? "Final" : "Stopped" : "Running" : complete ? "Not started" : "History incomplete" };

    }
}
