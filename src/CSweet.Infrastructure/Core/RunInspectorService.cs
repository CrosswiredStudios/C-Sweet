using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Extends the existing diagnostic authorization boundary without reconstructing private context.</summary>
public sealed class RunInspectorService(CSweetDbContext db, CurrentActivityService activity)
{
    public async Task<RunInspectorResponse> ReadAsync(Guid organization, Guid user, Guid employee,
        Guid? attempt, Guid? modelRun, Guid? workItem, long after, CancellationToken token)
    {
        if (attempt.HasValue == modelRun.HasValue || after < 0)
            throw new ArgumentException("Select one execution attempt or standalone model run.");
        // This authorizes employee diagnostics AND the exact canonical work before any additional projection.
        var page = attempt is { } attemptId
            ? await activity.FeedAsync(organization, user, employee, attemptId, workItem, after, token)
            : await activity.ModelFeedAsync(organization, user, employee, modelRun!.Value, after, token);
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && x.Id == employee)
            .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(token);
        // An employee outside this organization is a missing resource, not a server fault.
        if (installation is null) throw new KeyNotFoundException("The employee is not part of this organization.");
        var query = db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == organization && x.AgentInstallationId == installation &&
            (attempt.HasValue ? x.AgentWorkAttemptId == attempt && x.WorkItemId == workItem : x.Id == modelRun));
        var runs = await query.OrderBy(x => x.StartedAt).ThenBy(x => x.Id).Take(101).ToListAsync(token);
        if (!attempt.HasValue)
        {
            var actualWorkItem = runs.SingleOrDefault()?.WorkItemId;
            if (workItem.HasValue && workItem != actualWorkItem) throw new UnauthorizedAccessException();
            workItem = actualWorkItem;
        }
        var truncated = runs.Count > 100;
        if (truncated) runs.RemoveAt(100);
        var providerIds = runs.Select(x => x.ProviderProfileId).Distinct().ToArray();
        var names = await db.LlmProviderProfiles.AsNoTracking().Where(x => providerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var calls = runs.Select(x =>
        {
            var (stage, attempt) = Route(x.InferenceSettingsJson);
            return new RunInspectorModelCall(x.Id, names.GetValueOrDefault(x.ProviderProfileId), x.Model,
                x.Status, x.StartedAt, x.CompletedAt, x.DurationMs, x.TokenInputCount, x.TokenOutputCount, x.TokenCachedInputCount,
                x.MemoryErasedAt is null ? Bound(x.FailureMessage) : null, stage, attempt);
        }).ToArray();
        var nodes = calls.Select(x => new RunInspectorNode($"model:{x.Id:D}", "Model",
            $"{x.Model ?? "Model call"} · {x.Status}{(x.RouteAttempt > 0 ? $" · fallback {x.RouteAttempt}" : "")}{(x.Stage is { } s && s != "Default" ? $" · {s}" : "")}",
            x.StartedAt, null, x.Id, "AgentRunLog", (x.Provider ?? "Provider unavailable") + "\n\nInference configuration and transformations:\n" + CSweet.Infrastructure.Setup.AuditPayloadSanitizer.RedactJson(runs.Single(r => r.Id == x.Id).InferenceSettingsJson))).ToList();
        foreach (var entry in page.Entries)
        {
            var parts = entry.Key.Split(':');
            var hasModel = Guid.TryParse(parts[0] == "model" && parts.Length > 1 ? parts[1] : parts[0], out var runId) && runs.Any(x => x.Id == runId);
            var parent = hasModel ? $"model:{runId:D}" : null;
            // Only exact call identifiers establish a call/result edge. Time adjacency is not causality.
            if (hasModel && parts.Length >= 3 && parts[^1] == "function_result")
                parent = $"{runId}:{string.Join(':', parts.Skip(1).SkipLast(1))}:function_call";
            nodes.Add(new(entry.Key, entry.Kind, entry.Label, entry.OccurredAt, parent, entry.Id, "AuditEvent", entry.Text, entry.Append, entry.Append ? [new(entry.Id, entry.Sequence, entry.StreamSequence, entry.Text)] : null));
        }
        var grouped = nodes.GroupBy(x => x.Id).Select(RunInspectorNode.Merge)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArray();
        // Do not draw dangling graph edges when an earlier page or model call was omitted.
        var ids = grouped.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        grouped = grouped.Select(x => x with { ParentId = x.ParentId is { } p && ids.Contains(p) ? p : null }).ToArray();
        var usage = new RunInspectorUsage(runs.Sum(x => (long?)x.TokenInputCount ?? 0), runs.Sum(x => (long?)x.TokenOutputCount ?? 0),
            runs.Sum(x => (long?)x.TokenCachedInputCount ?? 0), runs.Sum(x => (long?)x.TokenReasoningCount ?? 0), runs.Count,
            runs.Count(x => x.TokenInputCount is null || x.TokenOutputCount is null));
        return new(employee, attempt, modelRun, workItem, grouped, usage, page.NextSequence, page.HasMore,
            page.EarlierEntriesOmitted || truncated, page.EvidenceUnavailable, calls);
    }

    // Older run logs predate routing evidence; treat them as the primary target of the Default stage.
    private static (string? Stage, int Attempt) Route(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return (null, 0);
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(settings);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, 0);
            var stage = root.TryGetProperty("stage", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() : null;
            var attempt = root.TryGetProperty("routeAttempt", out var a) && a.TryGetInt32(out var n) ? n : 0;
            return (stage, attempt);
        }
        catch (System.Text.Json.JsonException) { return (null, 0); }
    }

    private static string? Bound(string? text)
    {
        if (text is null) return null;
        var safe = CSweet.Infrastructure.Setup.AuditPayloadSanitizer.RedactText(text);
        return safe.Length > 1000 ? safe[..1000] : safe;
    }
}
