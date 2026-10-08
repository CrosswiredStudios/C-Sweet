using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private async Task<string?> CheckErasureDiagnosticCopiesAsync(IReadOnlyList<ChatTurn> turns, CancellationToken token)
    {
        var ids = turns.Select(x => x.Id).ToArray();
        var outputs = turns.Where(x => x.AssistantMessageId != null).Select(x => x.AssistantMessageId!.Value).ToArray();
        // External delivery payloads have independent obligations. Do not hide them
        // by calling a local memory cleanup complete.
        if (await db.CommunicationDeliveries.AnyAsync(x => x.ConversationMessageId != null && outputs.Contains(x.ConversationMessageId.Value), token))
            return "memory_erasure_external_delivery_review_required";
        var traces = await db.ChatTurnTraceEvents.AsNoTracking().Where(x => ids.Contains(x.ChatTurnId)).Select(x => x.Id).Take(32769).ToArrayAsync(token);
        if (traces.Length > 32768) throw new InvalidOperationException("memory_erasure_scan_limit");
        var related = ids.Concat(traces).ToArray();
        var audits = await db.AuditEvents.AsNoTracking().Where(x => x.EntityId != null && related.Contains(x.EntityId.Value))
            .OrderBy(x => x.Id).Take(8193).ToArrayAsync(token);
        var outbox = await db.AuditOutbox.AsNoTracking().Where(x => x.SourceEntityId != null && related.Contains(x.SourceEntityId.Value))
            .OrderBy(x => x.Id).Take(8193).ToArrayAsync(token);
        if (audits.Length + outbox.Length > 8192) throw new InvalidOperationException("memory_erasure_scan_limit");
        long bytes = 0;
        bool Safe(string? json)
        {
            if (json is null) return false;
            bytes += Encoding.UTF8.GetByteCount(json);
            if (bytes > 33_554_432) throw new InvalidOperationException("memory_erasure_scan_limit");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var expected = new HashSet<string> { "source", "evidence", "historical", "historyNotice" };
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(x => !expected.Remove(x.Name)) || expected.Count != 0 ||
                root.GetProperty("evidence").GetProperty("contentPolicy").GetString() != "memory-content-omitted-v1" ||
                root.GetProperty("evidence").EnumerateObject().Count() != 1) return false;
            var allowed = new HashSet<string> { "Id", "OrganizationId", "CreatedAt", "CompletedAt", "OccurredAt", "Attempt", "Sequence", "Status" };
            bool SafeValue(JsonProperty field) => field.Name switch
            {
                "Id" or "OrganizationId" => field.Value.ValueKind == JsonValueKind.String && Guid.TryParse(field.Value.GetString(), out _),
                "CreatedAt" or "OccurredAt" => field.Value.ValueKind == JsonValueKind.String && field.Value.TryGetDateTimeOffset(out _),
                "CompletedAt" => field.Value.ValueKind == JsonValueKind.Null || field.Value.ValueKind == JsonValueKind.String && field.Value.TryGetDateTimeOffset(out _),
                "Attempt" or "Sequence" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out _),
                "Status" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var status) && Enum.IsDefined((ChatTurnStatus)status),
                _ => false
            };
            return root.GetProperty("source").EnumerateObject().All(x => allowed.Contains(x.Name) && SafeValue(x)) &&
                root.GetProperty("historical").ValueKind is JsonValueKind.True or JsonValueKind.False &&
                (root.GetProperty("historyNotice").ValueKind == JsonValueKind.Null || root.GetProperty("historyNotice").GetString() ==
                    "Imported source snapshot; missing transitions and original versions cannot be reconstructed.");
        }
        try
        {
            bool SafeEvent(string eventType) => eventType is "chat.trace.recorded" or "audit.source.snapshot" ||
                Enum.GetValues<ChatTurnStatus>().Any(x => eventType == "chat.turn." + x.ToString().ToLowerInvariant());
            foreach (var item in outbox)
            {
                var json = item.ProtectedRequest is { Length: > 0 } protectedRequest
                    ? Encoding.UTF8.GetString((protection ?? db.AuditProtection ?? throw new CryptographicException()).CreateProtector("CSweet.AuditOutbox.v1").Unprotect(protectedRequest))
                    : item.RequestJson;
                var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(json);
                if (request is null || !SafeEvent(request.EventType) || request.MetadataJson is not null || request.ErrorMessage is not null ||
                    request.ErrorCode is not null || request.Summary != request.EventType || request.Payload is not { } payload ||
                    !Safe(Encoding.UTF8.GetString(payload.Span))) return "memory_erasure_diagnostics_review_required";
            }
            foreach (var item in audits)
            {
                if (!SafeEvent(item.EventType) || item.MetadataJson is not null || item.ErrorMessage is not null || item.ErrorCode is not null ||
                    item.Summary != item.EventType) return "memory_erasure_diagnostics_review_required";
                var payload = await db.AuditEventPayloads.AsNoTracking().SingleOrDefaultAsync(x => x.AuditEventId == item.Id, token);
                var json = payload is null ? item.PayloadPreview : Encoding.UTF8.GetString((protection ?? db.AuditProtection ?? throw new CryptographicException())
                    .CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent));
                if (!Safe(json)) return "memory_erasure_diagnostics_review_required";
            }
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            if (error.Message == "memory_erasure_scan_limit") throw;
            return "memory_erasure_diagnostics_review_required";
        }
        return null;
    }
}
