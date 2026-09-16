using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Security;
using CSweet.Domain.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

public sealed partial class SecurityAuditService
{
    private static bool IsModelResponse(AuditEvent item) => item.EntityType == "AgentRunLog" && item.EntityId.HasValue &&
        item.EventType is "model.response.chunk" or "model.call.completed";

    private async Task<ModelResponseAuditResponse> AssembleModelResponseAsync(Guid organizationId, Guid runId,
        Guid? employeeId, CancellationToken token)
    {
        const int maximumChunks = 10000, maximumCharacters = 2_000_000;
        var events = db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.EntityType == "AgentRunLog" && x.EntityId == runId && x.EventType == "model.response.chunk");
        if (employeeId is Guid employee)
            events = events.Where(x => x.ActorOrganizationUserId == employee || db.AuditEventEmployees.Any(a =>
                a.AuditEventId == x.Id && a.OrganizationId == organizationId && a.EmployeeId == employee));
        var watermark = await events.MaxAsync(x => (long?)x.Sequence, token);
        events = events.Where(x => x.Sequence <= watermark);
        var count = await events.CountAsync(token);
        var inspected = 0;
        var characters = 0;
        var incomplete = false;
        var budgetReached = false;
        var integrity = "Verified";
        var chunks = new List<(long Order, long LedgerOrder, JsonElement Payload)>();
        var sources = new List<ModelResponseAuditSource>();
        // Fetch protected evidence in bounded batches, not one database query per token.
        while (inspected < Math.Min(count, maximumChunks) && characters < maximumCharacters)
        {
            var batch = await events.OrderBy(x => x.Sequence).Skip(inspected)
                .Take(Math.Min(100, maximumChunks - inspected)).ToListAsync(token);
            if (batch.Count == 0) break;
            var ids = batch.Select(x => x.Id).ToArray();
            var payloads = await db.AuditEventPayloads.AsNoTracking().Where(x => ids.Contains(x.AuditEventId))
                .ToDictionaryAsync(x => x.AuditEventId, token);
            var hashes = batch.Select(x => x.PreviousRecordHash).Where(x => x != null).ToArray();
            var previousHashes = (await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId && hashes.Contains(x.RecordHash))
                .Select(x => x.RecordHash).ToListAsync(token)).ToHashSet();
            foreach (var item in batch)
            {
                if (characters >= maximumCharacters) break;
                inspected++;
                if (sources.Count < 100) sources.Add(new(item.Id, item.OccurredAt, item.EventType));
                var status = IntegrityStatus(item, item.PreviousRecordHash == null || previousHashes.Contains(item.PreviousRecordHash));
                if (status != "Verified") { integrity = status == "Invalid" ? "Invalid" : integrity == "Invalid" ? integrity : "Unverified"; incomplete = true; }
                string? content = null;
                if (payloads.TryGetValue(item.Id, out var payload))
                {
                    try
                    {
                        var bytes = dataProtectionProvider.CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent);
                        if (Convert.ToHexString(SHA256.HashData(bytes)) != item.EvidenceSha256) { integrity = "Invalid"; incomplete = true; continue; }
                        content = Encoding.UTF8.GetString(bytes);
                    }
                    catch (CryptographicException) { integrity = "Invalid"; incomplete = true; continue; }
                }
                else { content = item.PayloadPreview; incomplete = true; }
                if (content == null) { incomplete = true; continue; }
                if (characters + content.Length > maximumCharacters) { incomplete = true; budgetReached = true; break; }
                characters += content.Length;
                try
                {
                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { incomplete = true; continue; }
                    var sequence = root.TryGetProperty("sequence", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var value) ? value : item.Sequence;
                    chunks.Add((sequence, item.Sequence, root.Clone()));
                }
                catch (JsonException) { incomplete = true; }
            }
            if (budgetReached || characters >= maximumCharacters) break;
        }
        var text = new StringBuilder();
        var contents = new List<JsonElement>();
        long? input = null, output = null;
        var empty = 0;
        foreach (var chunk in chunks.OrderBy(x => x.Order).ThenBy(x => x.LedgerOrder))
        {
            var root = chunk.Payload;
            var chunkText = root.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var hasContent = !string.IsNullOrEmpty(chunkText);
            text.Append(chunkText);
            if (root.TryGetProperty("contents", out var parts) && parts.ValueKind == JsonValueKind.Array)
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "text")
                    {
                        // update.Text already contains these text parts; do not duplicate them.
                        if (string.IsNullOrEmpty(chunkText) && part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                        { text.Append(partText.GetString()); hasContent |= !string.IsNullOrEmpty(partText.GetString()); }
                    }
                    else { contents.Add(part.Clone()); hasContent = true; }
                }
            if (!hasContent) empty++;
            if (root.TryGetProperty("inputTokens", out var inputValue) && inputValue.ValueKind == JsonValueKind.Number && inputValue.TryGetInt64(out var inputCount)) input = inputCount;
            if (root.TryGetProperty("outputTokens", out var outputValue) && outputValue.ValueKind == JsonValueKind.Number && outputValue.TryGetInt64(out var outputCount)) output = outputCount;
        }
        incomplete |= inspected < count;
        if (inspected < count && integrity == "Verified") integrity = "PartiallyVerified";
        return new(count, inspected, empty, text.ToString(), contents, input, output, integrity, incomplete, sources);
    }
}
