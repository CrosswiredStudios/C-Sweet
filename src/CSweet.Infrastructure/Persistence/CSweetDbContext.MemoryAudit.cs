using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    // Scoped by the internal erasure writer while it stages reset/settlement. Historical
    // or incomplete read evidence must not cause cleanup itself to copy more content.
    internal bool OmitWorkAuditContent { get; set; }
    private static readonly HashSet<string> ContentFreeWorkAuditFields = new(StringComparer.Ordinal)
    {
        "Id", "OrganizationId", "AgentInstallationId", "AgentWorkItemId", "AgentWorkAttemptId", "RuntimeInstanceId",
        "Kind", "Status", "Attempt", "AttemptCount", "MaximumAttempts", "Sequence", "LastProgressSequence", "SizeBytes",
        "CreatedAt", "CompletedAt", "AvailableAt", "DeadlineAt", "ClaimedAt", "FinishedAt", "LastConfirmedAt", "LeaseExpiresAt",
        "OccurredAt", "PayloadHash", "ResultHash", "CompletionHash"
    };

    private static bool IsAuditDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private bool RequiresContentFreeWorkAudit(AgentWorkItem? work)
    {
        if (OmitWorkAuditContent || work is null || work.MemoryErasedAt is not null || work.MemoryRecallReceiptJson is not null || work.SourceType == "chat-turn") return true;
        var persisted = AgentWorkAttempts.AsNoTracking().Where(x => x.AgentWorkItemId == work.Id)
            .Select(x => x.RuntimeInstanceId).Take(129).ToArray();
        var tracked = ChangeTracker.Entries<AgentWorkAttempt>().Where(x => x.Entity.AgentWorkItemId == work.Id)
            .Select(x => x.Entity.RuntimeInstanceId).Take(129).ToArray();
        // Bound before deduplicating: many attempts on one runtime must not hide a later
        // attempt on another runtime whose retained memory would otherwise be missed.
        if (persisted.Length > 128 || tracked.Length > 128) return true;
        var runtimeIds = persisted.Concat(tracked).Distinct().ToArray();
        if (ChangeTracker.Entries<AgentMemoryReadReceipt>().Any(x => x.Entity.WorkId == work.Id || runtimeIds.Contains(x.Entity.RuntimeId)) ||
            AgentMemoryReadReceipts.AsNoTracking().Any(x => x.WorkId == work.Id || runtimeIds.Contains(x.RuntimeId))) return true;
        if (runtimeIds.Length == 0) return work.AttemptCount > 0;
        var knownRuntimes = AgentRuntimeInstances.AsNoTracking().Where(x => runtimeIds.Contains(x.Id)).Select(x => x.Id).ToArray()
            .Concat(ChangeTracker.Entries<AgentRuntimeInstance>().Select(x => x.Entity.Id)).ToHashSet();
        if (runtimeIds.Any(x => !knownRuntimes.Contains(x))) return true;
        // Later work can echo memory retained from earlier work on the same runtime.
        // Older runtimes without complete read evidence cannot prove content is absent.
        if (ChangeTracker.Entries<AgentRuntimeInstance>().Any(x => runtimeIds.Contains(x.Entity.Id) && x.Entity.MemoryReadEvidenceVersion != AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion) ||
            AgentRuntimeInstances.AsNoTracking().Any(x => runtimeIds.Contains(x.Id) && x.MemoryReadEvidenceVersion != AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion)) return true;
        if (AgentWorkAttempts.AsNoTracking().Any(x => runtimeIds.Contains(x.RuntimeInstanceId) &&
            (x.AgentWorkItem!.MemoryRecallReceiptJson != null || x.AgentWorkItem.SourceType == "chat-turn"))) return true;
        var localWork = ChangeTracker.Entries<AgentWorkItem>().Where(x => x.Entity.MemoryRecallReceiptJson is not null || x.Entity.SourceType == "chat-turn")
            .Select(x => x.Entity.Id).ToHashSet();
        return ChangeTracker.Entries<AgentWorkAttempt>().Any(x => runtimeIds.Contains(x.Entity.RuntimeInstanceId) && localWork.Contains(x.Entity.AgentWorkItemId));
    }
}
