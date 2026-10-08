using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentWorkInbox
{
    internal static string ErasedIdempotencyKey(string original) => "memory-erased:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));

    // Only MemoryWorkErasure calls this after inventory, locks and full-closure review.
    internal async Task<(int Works, int Progress)> StageMemoryErasureAsync(Guid[] workIds, CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Work erasure requires a caller-owned PostgreSQL transaction.");
        var items = await db.AgentWorkItems.AsNoTracking().Where(x => workIds.Contains(x.Id) && x.MemoryErasedAt == null)
            .Select(x => new AgentWorkItem { Id = x.Id, OrganizationId = x.OrganizationId, AgentInstallationId = x.AgentInstallationId,
                Kind = x.Kind, Status = x.Status, AttemptCount = x.AttemptCount, SourceType = x.SourceType, SourceId = x.SourceId,
                CorrelationId = x.CorrelationId, IdempotencyKey = x.IdempotencyKey }).ToListAsync(token);
        var now = timeProvider.GetUtcNow();
        foreach (var item in items.Where(x => x.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased))
        {
            item.Status = AgentWorkStatus.DeadLetter;
            await RecordTicketFailureAsync(item, item.AttemptCount, MemoryWorkErasure.SafeMessage, now, token);
            await FailCoordinationForWorkAsync(item, MemoryWorkErasure.SafeMessage, now, token);
            if (item.Kind == AgentWorkKind.ConfigurationUpdate)
                (await db.AgentInstallations.SingleAsync(x => x.Id == item.AgentInstallationId, token)).ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart;
        }
        // Save reset fencing, settlement and outbox effects before the tombstone rejects
        // all future writes. Save is not commit; a later audit failure rolls all of it back.
        var omitContent = db.OmitWorkAuditContent;
        try { db.OmitWorkAuditContent = true; await db.SaveChangesAsync(token); }
        finally { db.OmitWorkAuditContent = omitContent; }
        var selected = items.Select(x => x.Id).ToArray();
        var progress = await db.AgentWorkProgress.Where(x => selected.Contains(x.AgentWorkItemId)).ExecuteDeleteAsync(token);
        var result = JsonSerializer.SerializeToUtf8Bytes(new AgentWorkCompletion(false, null, MemoryWorkErasure.SafeMessage, MemoryWorkErasure.FailureCode, false));
        var resultHash = Convert.ToHexString(SHA256.HashData(result));
        await db.AgentWorkAttempts.Where(x => selected.Contains(x.AgentWorkItemId)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FinishedAt, x => x.FinishedAt ?? now)
            .SetProperty(x => x.Error, MemoryWorkErasure.FailureCode)
            .SetProperty(x => x.CompletionHash, resultHash)
            .SetProperty(x => x.LeaseTokenHash, "memory-erased"), token);
        foreach (var item in items)
        {
            var payload = _protector.Protect("{}"u8.ToArray()); var completion = _protector.Protect(result);
            var key = ErasedIdempotencyKey(item.IdempotencyKey); var correlation = item.Id.ToString("D");
            await db.AgentWorkItems.Where(x => x.Id == item.Id && x.MemoryErasedAt == null).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProtectedPayload, payload).SetProperty(x => x.ProtectedResult, completion)
                .SetProperty(x => x.ResultHash, resultHash).SetProperty(x => x.MemoryErasedAt, now)
                .SetProperty(x => x.Name, "memory.erased").SetProperty(x => x.CorrelationId, correlation)
                .SetProperty(x => x.CausationId, (string?)null).SetProperty(x => x.SourceType, (string?)null).SetProperty(x => x.SourceId, (string?)null)
                .SetProperty(x => x.IdempotencyKey, key).SetProperty(x => x.LastError, MemoryWorkErasure.SafeMessage)
                .SetProperty(x => x.Status, x => x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased ? AgentWorkStatus.DeadLetter : x.Status)
                .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? now), token);
        }
        return (items.Count, progress);
    }
}
