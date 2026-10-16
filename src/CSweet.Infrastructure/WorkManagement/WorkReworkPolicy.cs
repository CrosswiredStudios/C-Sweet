using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Review cycles spend a stagnation budget, not a lifetime iteration budget.</summary>
internal static class WorkReworkPolicy
{
    internal const string LegacyLimitReason = "The workflow traversal limit was reached.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Decision(bool Allowed, bool ReviewRework, int StagnantReviews, string? Reason);

    internal static Decision Evaluate(WorkStageExecution stage, WorkOrchestrationTransition transition,
        string outcomeCode, JsonElement output)
    {
        if (transition.MaximumTraversals is not { } limit) return new(true, false, 0, null);
        if (stage.StageKey is not ("quality" or "technical-review") ||
            transition.ToStageKey is not ("development" or "specialist-execution") ||
            outcomeCode is not ("changes_requested" or "failed" or "rejected"))
            return new(stage.ItemExecution!.Traversal + 1 <= limit, false, 0, LegacyLimitReason);

        Snapshot? best = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagnant = 0;
        var hasBaseline = false;
        void Observe(Snapshot? current)
        {
            var fresh = current is not null && seen.Add(current.Candidate);
            var progress = fresh && (best is null || current!.Improves(best));
            if (progress) { best = current; stagnant = 0; }
            else if (hasBaseline) stagnant++;
            hasBaseline = true;
        }

        // One observation per completed review cycle. Retries are not rework cycles.
        foreach (var prior in stage.ItemExecution!.Stages.Where(x => x.Id != stage.Id &&
                     x.StageKey == stage.StageKey && x.Traversal < stage.Traversal)
                     .OrderBy(x => x.Traversal).ThenBy(x => x.CreatedAt))
        {
            var outcome = CompletedOutcome(prior);
            if (outcome?.OutcomeCode is "changes_requested" or "failed" or "rejected")
                Observe(Read(outcome.Output, stage.StageKey));
        }
        Observe(Read(output, stage.StageKey));
        return new(stagnant < limit, true, stagnant, stagnant < limit ? null :
            $"Review rework needs a management decision after {stagnant} consecutive cycles without measurable progress. " +
            "No new candidate improved the best reviewed acceptance criteria or reduced findings without regressing those criteria. " +
            "Review the retained findings and provide direction before retrying.");
    }

    internal static Shared.WorkExecutionOutcomeV1? CompletedOutcome(WorkStageExecution stage)
    {
        var attempt = stage.Attempts.OrderByDescending(x => x.Attempt).FirstOrDefault();
        if (attempt?.Status != WorkExecutionAttemptStatus.Completed || attempt.ResultJson is null) return null;
        try
        {
            var result = JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(attempt.ResultJson, Json);
            return result?.StageExecutionId == stage.Id && result.AttemptId == attempt.Id &&
                result.Disposition == Shared.WorkExecutionDispositions.Completed ? result : null;
        }
        catch (JsonException) { return null; }
    }

    internal static async Task QueueEscalationAsync(CSweetDbContext db, WorkStageExecution stage, Decision decision,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var item = stage.ItemExecution!;
        var execution = item.SprintExecution!;
        var key = $"rework-stalled:{stage.Id:N}";
        if (await db.WorkOrchestrationEvents.AnyAsync(x => x.OrganizationId == execution.OrganizationId &&
                x.IdempotencyKey == key, cancellationToken)) return;
        db.WorkOrchestrationEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
            BoardId = execution.BoardId, SprintExecutionId = execution.Id, ItemExecutionId = item.Id,
            StageExecutionId = stage.Id, EventType = "stage.rework.stalled", IdempotencyKey = key, OccurredAt = now,
            DataJson = JsonSerializer.Serialize(new { decision.StagnantReviews, decision.Reason }, Json) });
        var manager = await db.WorkBoards.Where(x => x.Id == execution.BoardId && x.OrganizationId == execution.OrganizationId)
            .Select(x => x.ManagerOrganizationUserId).SingleOrDefaultAsync(cancellationToken);
        if (manager is { } recipient)
            db.UserNotifications.Add(new() { Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                RecipientOrganizationUserId = recipient, Severity = NotificationSeverity.Important,
                Category = "WorkReworkStalled", Title = $"Review rework needs direction: {item.ItemIdentifier}",
                Body = decision.Reason!, ActionUri = $"/organizations/{execution.OrganizationId:D}/work",
                CreatedAt = now, DeduplicationKey = key });
        // The caller commits this event, notification and its realtime outbox with the blocked state.
    }

    private sealed record Snapshot(string Candidate, Dictionary<string, bool> Criteria, int Findings)
    {
        internal bool Improves(Snapshot prior) => Criteria.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(prior.Criteria.Keys) &&
            prior.Criteria.All(x => !x.Value || Criteria[x.Key]) &&
            (Criteria.Count(x => x.Value) > prior.Criteria.Count(x => x.Value) || Findings < prior.Findings);
    }

    private static Snapshot? Read(JsonElement output, string stageKey)
    {
        if (output.ValueKind != JsonValueKind.Object) return null;
        string? candidate = null;
        foreach (var name in new[] { "sha256", "candidateDigest", "commitSha", "sourceCommitSha" })
            if (output.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: >= 40 } digest && digest.All(Uri.IsHexDigit))
            { candidate = digest; break; }
        if (candidate is null || !output.TryGetProperty("findings", out var findings) ||
            findings.ValueKind != JsonValueKind.Array) return null;
        var criteria = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (output.TryGetProperty("criteria", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("criterion", out var name) ||
                    name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                    !entry.TryGetProperty("satisfied", out var satisfied) ||
                    satisfied.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !criteria.TryAdd(name.GetString()!.Trim(), satisfied.GetBoolean())) return null;
            }
        }
        if (stageKey == "quality" && criteria.Count == 0) return null;
        return new(candidate, criteria, findings.GetArrayLength());
    }
}
