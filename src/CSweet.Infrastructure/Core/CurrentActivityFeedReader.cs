using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Only call after authorizing both employee diagnostics and the execution's task.</summary>
public sealed class CurrentActivityFeedReader(CSweetDbContext db, IDataProtectionProvider protection)
{
    public async Task<IReadOnlyList<CurrentActivityStep>> ProgressAsync(Guid attemptId, Guid? taskId, int limit, CancellationToken token)
    {
        var records = await db.AgentWorkProgress.AsNoTracking().Where(x => x.AgentWorkAttemptId == attemptId)
            .OrderByDescending(x => x.Sequence).Take(20).ToListAsync(token);
        var steps = new List<CurrentActivityStep>();
        foreach (var record in records)
        {
            try
            {
                using var payload = JsonDocument.Parse(protection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(record.ProtectedValue));
                if (!MatchesItem(payload.RootElement, taskId)) continue;
                var text = ProgressText(payload.RootElement);
                if (!string.IsNullOrWhiteSpace(text)) steps.Add(new(record.Id.ToString(), "Progress", Bound(text, 500), record.OccurredAt));
            }
            catch (Exception error) when (error is CryptographicException or JsonException) { }
        }
        return steps.DistinctBy(x => x.Text).Take(limit).ToArray();
    }

    public async Task<CurrentActivityFeedPage> ReadAsync(Guid organizationId, Guid employeeId, AgentWorkAttempt? attempt,
        Guid? taskId, long after, CancellationToken token, Guid? runId = null)
    {
        const int batchSize = 150;
        var attemptId = attempt?.Id ?? Guid.Empty;
        var installationId = attempt?.AgentWorkItem?.AgentInstallationId;
        var runs = db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.WorkItemId == taskId &&
            (runId.HasValue ? x.Id == runId : x.AgentWorkAttemptId == attemptId && x.AgentInstallationId == installationId));
        var runIds = runs.Select(x => x.Id);
        var progressIds = db.AgentWorkProgress.Where(x => attempt != null && x.AgentWorkAttemptId == attemptId).Select(x => x.Id);
        var query = db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.EntityId.HasValue &&
            ((x.EntityType == "AgentRunLog" && runIds.Contains(x.EntityId.Value) &&
                (x.EventType == "model.response.chunk" || x.EventType == "model.call.started" || x.EventType == "model.call.completed")) ||
             (x.EntityType == "AgentWorkProgress" && progressIds.Contains(x.EntityId.Value) && x.EventType == "agent.work.progress")));
        var batch = after == 0 ? await query.OrderByDescending(x => x.Sequence).Take(batchSize + 1).ToListAsync(token) :
            await query.Where(x => x.Sequence > after).OrderBy(x => x.Sequence).Take(batchSize + 1).ToListAsync(token);
        var extra = batch.Count > batchSize;
        if (extra) batch.RemoveAt(batch.Count - 1);
        batch = batch.OrderBy(x => x.Sequence).ToList();
        var ids = batch.Select(x => x.Id).ToArray();
        var payloads = await db.AuditEventPayloads.AsNoTracking().Where(x => ids.Contains(x.AuditEventId)).ToDictionaryAsync(x => x.AuditEventId, token);
        var entries = new List<CurrentActivityFeedEntry>();
        var unavailable = false;
        foreach (var record in batch)
        {
            // Protected evidence is checked before projecting it into a less privileged UI surface.
            if (!payloads.TryGetValue(record.Id, out var payload)) { unavailable = true; continue; }
            try
            {
                var bytes = protection.CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != record.EvidenceSha256) { unavailable = true; continue; }
                if (record.IntegrityVersion > 0 && (AuditIntegrity.ComputeRecordHash(record) != record.RecordHash ||
                    protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(record.IntegritySeal!) != record.RecordHash))
                { unavailable = true; continue; }
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (record.EventType == "agent.work.progress")
                {
                    var evidence = Property(root, "evidence");
                    if (!MatchesItem(evidence, taskId)) continue;
                    var message = ProgressText(evidence);
                    if (!string.IsNullOrWhiteSpace(message)) entries.Add(Entry(record, record.Id.ToString(), "Progress", "Agent progress", message));
                }
                else if (record.EventType == "model.response.chunk") entries.AddRange(ParseModelChunk(record, root));
                else entries.Add(Entry(record, $"model:{record.EntityId}:status", "Model", "Model call",
                    record.EventType == "model.call.started" ? "Model call started" : $"Model call {record.Outcome.ToLowerInvariant()}"));
            }
            catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
            { unavailable = true; }
        }
        return new(entries, batch.Count == 0 ? after : batch[^1].Sequence, after != 0 && extra, after == 0 && extra, unavailable);
    }

    internal static IReadOnlyList<CurrentActivityFeedEntry> ParseModelChunk(AuditEvent record, JsonElement root)
    {
        var entries = new List<CurrentActivityFeedEntry>();
        var sequence = Property(root, "sequence");
        long? stream = sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt64(out var value) ? value : null;
        var reasoning = new StringBuilder();
        var text = new StringBuilder(Text(root, "text"));
        var parts = Property(root, "contents");
        if (parts.ValueKind == JsonValueKind.Array)
            foreach (var part in parts.EnumerateArray())
            {
                var kind = Text(part, "kind");
                if (kind == "reasoning") reasoning.Append(Text(part, "text"));
                else if (kind == "text" && string.IsNullOrEmpty(Text(root, "text"))) text.Append(Text(part, "text"));
                else if (kind is "function_call" or "function_result")
                {
                    var call = Text(part, "callId") ?? record.Id.ToString();
                    var body = kind == "function_call" ? $"Requested {Text(part, "name") ?? "tool"}" :
                        AuditPayloadSanitizer.RedactJson(Property(part, "result").ValueKind == JsonValueKind.Undefined ? "null" : Property(part, "result").GetRawText()) ?? "Result unavailable";
                    entries.Add(Entry(record, $"{record.EntityId}:{call}:{kind}", "Tool", kind == "function_call" ? "Tool requested" : "Tool result", body));
                }
            }
        if (reasoning.Length > 0) entries.Add(Entry(record, $"{record.EntityId}:reasoning", "Reasoning", "Provider reasoning", reasoning.ToString(), true, stream));
        if (text.Length > 0) entries.Add(Entry(record, $"{record.EntityId}:text", "Output", "Model output", text.ToString(), true, stream));
        return entries;
    }

    private static CurrentActivityFeedEntry Entry(AuditEvent record, string key, string kind, string label, string text,
        bool append = false, long? stream = null) => new(record.Id, record.Sequence, key, kind, label, Bound(text, 8000), record.OccurredAt, append, stream);

    // Item IDs in untrusted progress are a narrowing filter, never a grant or execution attribution.
    private static bool MatchesItem(JsonElement root, Guid? taskId)
    {
        var id = Property(root, "itemId");
        return taskId.HasValue ? id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var value) && value == taskId : id.ValueKind == JsonValueKind.Undefined;
    }
    private static string? ProgressText(JsonElement root) => Text(root, "message") ??
        (Text(root, "kind") is "progress" or "activity" ? Text(root, "delta") : null);
    private static string Bound(string text, int length)
    {
        var safe = AuditPayloadSanitizer.RedactText(text);
        return safe.Length <= length ? safe : safe[..length] + "\n[Display truncated]";
    }
    private static string? Text(JsonElement root, string key) => Property(root, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static JsonElement Property(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object ?
        root.EnumerateObject().FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : default;
}
