using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal sealed partial class MemoryWorkErasure
{
    private const string ModelReviewRequired = "memory_erasure_diagnostics_review_required";

    private async Task<ModelDiagnostics> ReadModelDiagnosticsAsync(Guid organization, Guid[] works, CancellationToken token)
    {
        if (works.Length == 0) return new([], new Dictionary<Guid, string>());
        var sourceIds = await db.AgentWorkItems.AsNoTracking().Where(x => works.Contains(x.Id) && x.SourceType == "chat-turn")
            .Select(x => x.SourceId).ToArrayAsync(token);
        var turns = sourceIds.Select(x => Guid.TryParseExact(x, "D", out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
        var logs = await db.AgentRunLogs.AsNoTracking().Where(x => x.AgentWorkItemId != null && works.Contains(x.AgentWorkItemId.Value) ||
            x.ChatTurnId != null && turns.Contains(x.ChatTurnId.Value)).OrderBy(x => x.Id).Take(1025).ToArrayAsync(token);
        if (logs.Length > 1024) throw Limit();
        foreach (var log in logs)
        {
            // Caller chat telemetry is a discovery hint, not authority to scrub another
            // work's inference or to invent missing attribution in a historical model run.
            if (log.AgentWorkItemId is not { } workId || !works.Contains(workId) || log.OrganizationId != organization ||
                log.AgentInstallationId is not { } installation || log.EmployeeId is not { } employee ||
                !await db.AgentWorkItems.AnyAsync(x => x.Id == workId && x.AgentInstallationId == installation && x.OrganizationId == organization.ToString("D"), token) ||
                !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == employee && x.OrganizationId == organization && x.AgentInstallationId == installation, token))
                throw new InvalidOperationException(ModelReviewRequired);
        }
        var ids = logs.Select(x => x.Id).ToArray();
        var outbox = await db.AuditOutbox.AsNoTracking().Where(x => x.SourceEntityId != null && ids.Contains(x.SourceEntityId.Value))
            .OrderBy(x => x.Id).Take(8193).ToArrayAsync(token);
        var audits = await db.AuditEvents.AsNoTracking().Where(x => x.EntityId != null && ids.Contains(x.EntityId.Value))
            .OrderBy(x => x.Id).Take(8193).ToArrayAsync(token);
        if (audits.Length + outbox.Length > 8192) throw Limit();
        var permitted = logs.ToDictionary(x => x.Id, _ => new SortedDictionary<string, object>(StringComparer.Ordinal));
        void Permit(Guid model, Guid eventId, string eventType, ReadOnlyMemory<byte> body, string? originalHash = null)
        {
            var captured = AuditPayloadSanitizer.Capture(body, "application/json");
            var evidence = new { eventType, payloadHash = originalHash?.ToUpperInvariant() ?? captured.Sha256,
                previewHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(captured.Preview ?? ""))),
                evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(captured.FullContent ?? ""))) };
            var key = eventId.ToString("D");
            if (permitted[model].TryGetValue(key, out var previous) && JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(evidence))
                throw new InvalidOperationException(ModelReviewRequired);
            permitted[model][key] = evidence;
        }
        long bytes = 0;
        string ReadBytes(byte[] value)
        {
            bytes += value.Length;
            if (bytes > 33_554_432) throw Limit();
            return Encoding.UTF8.GetString(value);
        }
        try
        {
            foreach (var copy in outbox)
            {
                var json = copy.ProtectedRequest is { Length: > 0 } encrypted
                    ? ReadBytes(Protector("CSweet.AuditOutbox.v1").Unprotect(encrypted)) : ReadBytes(Encoding.UTF8.GetBytes(copy.RequestJson));
                var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(json);
                if (request is null || request.OrganizationId != organization || request.EntityType != nameof(AgentRunLog) ||
                    request.EntityId != copy.SourceEntityId || request.Payload is not { } payload ||
                    !SafeModelMetadata(request.EventType, request.Summary, request.MetadataJson, request.ErrorCode, request.ErrorMessage) ||
                    !SafeModelEvidence(ReadBytes(payload.ToArray()), request.EventType)) throw new InvalidOperationException(ModelReviewRequired);
                if (request.EventId != copy.Id) throw new InvalidOperationException(ModelReviewRequired);
                Permit(copy.SourceEntityId!.Value, copy.Id, request.EventType, payload);
            }
            foreach (var copy in audits)
            {
                if (copy.OrganizationId != organization || copy.EntityType != nameof(AgentRunLog) ||
                    !SafeModelMetadata(copy.EventType, copy.Summary, copy.MetadataJson, copy.ErrorCode, copy.ErrorMessage))
                    throw new InvalidOperationException(ModelReviewRequired);
                var payload = await db.AuditEventPayloads.AsNoTracking().SingleOrDefaultAsync(x => x.AuditEventId == copy.Id, token);
                var json = payload is null ? ReadBytes(Encoding.UTF8.GetBytes(copy.PayloadPreview ?? ""))
                    : ReadBytes(Protector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent));
                if (!SafeModelEvidence(json, copy.EventType)) throw new InvalidOperationException(ModelReviewRequired);
                Permit(copy.EntityId!.Value, copy.Id, copy.EventType, Encoding.UTF8.GetBytes(json), copy.PayloadSha256);
            }
        }
        catch (Exception error) when (error is CryptographicException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            if (error.Message == "memory_erasure_scan_limit") throw;
            throw new InvalidOperationException(ModelReviewRequired);
        }
        var runs = logs.Where(x => x.MemoryErasedAt is null).Select(x => x.Id).ToArray();
        var receipts = runs.ToDictionary(x => x, x => JsonSerializer.Serialize(permitted[x]));
        if (receipts.Values.Any(x => x.Length > 262144)) throw Limit();
        return new(runs, receipts);
    }

    private IDataProtector Protector(string purpose) => (diagnosticProtection ?? protection ?? db.AuditProtection ?? throw new CryptographicException()).CreateProtector(purpose);
    private static bool SafeModelMetadata(string type, string? summary, string? metadata, string? code, string? error) =>
        type is "model.call.started" or "model.call.completed" or "model.response.chunk" or "audit.source.snapshot" &&
        metadata is null && code is null && error is null && (summary == type || type == "model.response.chunk" && summary is null);

    private static bool SafeModelEvidence(string json, string type)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        bool Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                return value.EnumerateObject().All(x => names.Add(x.Name) && Unique(x.Value));
            }
            return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(Unique);
        }
        if (root.ValueKind != JsonValueKind.Object || !Unique(root)) return false;
        if (type == "model.response.chunk")
        {
            var allowed = new HashSet<string>(["sequence", "contentPolicy", "inputTokens", "outputTokens", "role", "finishReason"], StringComparer.Ordinal);
            return root.TryGetProperty("contentPolicy", out var policy) && policy.GetString() == "memory-content-omitted-v1" &&
                root.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt64(out _) &&
                root.EnumerateObject().All(x => allowed.Contains(x.Name) && (x.Name switch
                {
                    "contentPolicy" => x.Value.ValueKind == JsonValueKind.String,
                    "role" => x.Value.ValueKind == JsonValueKind.Null || x.Value.ValueKind == JsonValueKind.String &&
                        new[] { "assistant", "user", "system", "tool" }.Contains(x.Value.GetString(), StringComparer.OrdinalIgnoreCase),
                    "finishReason" => x.Value.ValueKind == JsonValueKind.Null || x.Value.ValueKind == JsonValueKind.String &&
                        new[] { "stop", "length", "tool_calls", "content_filter" }.Contains(x.Value.GetString(), StringComparer.OrdinalIgnoreCase),
                    _ => x.Value.ValueKind == JsonValueKind.Null || x.Value.ValueKind == JsonValueKind.Number && x.Value.TryGetInt64(out _) ||
                        x.Name is "inputTokens" or "outputTokens" && x.Value.ValueKind == JsonValueKind.String && x.Value.GetString() == "[REDACTED]"
                }));
        }
        if (root.EnumerateObject().Count() != 4 || !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object || evidence.EnumerateObject().Count() != 1 ||
            !evidence.TryGetProperty("contentPolicy", out var omitted) || omitted.GetString() != "memory-content-omitted-v1" ||
            !root.TryGetProperty("historical", out var historical) || historical.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("historyNotice", out var notice) || notice.ValueKind != JsonValueKind.Null && notice.GetString() !=
                "Imported source snapshot; missing transitions and original versions cannot be reconstructed.") return false;
        return source.EnumerateObject().All(x => CSweetDbContext.IsContentFreeModelAuditField(x.Name) && (x.Value.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Number => x.Value.TryGetInt64(out _),
            JsonValueKind.String => x.Name.Contains("Token", StringComparison.Ordinal) && x.Value.GetString() == "[REDACTED]" ||
                Guid.TryParse(x.Value.GetString(), out _) || x.Value.TryGetDateTimeOffset(out _) ||
                x.Value.GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit),
            _ => false
        }));
    }

    private async Task StageModelDiagnosticsAsync(IReadOnlyDictionary<Guid, string> receipts, CancellationToken token)
    {
        if (receipts.Count == 0) return;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        foreach (var (id, receipt) in receipts)
        await db.AgentRunLogs.Where(x => x.Id == id && x.MemoryErasedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.MemoryErasedAt, now).SetProperty(x => x.MemoryErasureAuditJson, receipt).SetProperty(x => x.PromptPreview, (string?)null)
            .SetProperty(x => x.OutputPreview, (string?)null).SetProperty(x => x.FailureMessage, (string?)null)
            .SetProperty(x => x.InferenceSettingsJson, (string?)null).SetProperty(x => x.UsageAdditionalCountsJson, (string?)null), token);
    }
}
