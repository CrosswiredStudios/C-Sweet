using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Read-only host discovery. Caller holds work/source/memory barriers and authorizes the returned evidence.</summary>
internal sealed class MemoryErasureReadInventory(CSweetDbContext db)
{
    internal sealed record WorkRead(Guid WorkId, Guid InstallationId, MemoryRecallDispatchEvidence.ErasureRead Read);
    internal sealed record RuntimeRead(Guid RuntimeId, Guid WorkId, Guid EmployeeId, Guid InstallationId, MemoryRecallDispatchEvidence.ErasureRead Read);
    internal sealed record Inventory(IReadOnlyList<WorkRead> Queued, IReadOnlyList<RuntimeRead> Delivered,
        IReadOnlyList<Guid> WorkIds, IReadOnlyList<Guid> RuntimeIds);

    internal async Task<Inventory> ReadAsync(Guid organization, IReadOnlyCollection<MemoryErasureTarget> targets, CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Read erasure inventory requires a caller-owned PostgreSQL transaction.");
        var org = organization.ToString("D"); var affected = targets.ToHashSet();
        var queued = new List<WorkRead>(); var delivered = new List<RuntimeRead>();
        var workIds = new HashSet<Guid>(); var runtimeIds = new HashSet<Guid>();
        var unknown = false; long characters = 0; var count = 0;
        void Bound(string? json)
        {
            characters += json?.Length ?? 0;
            if (++count > 8192 || characters > 16_777_216) throw new InvalidOperationException("memory_erasure_scan_limit");
        }
        var works = db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == org && x.MemoryErasedAt == null)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.AgentInstallationId, x.SourceType, x.SourceId, x.PayloadHash, x.MemoryRecallReceiptJson }).Take(8193);
        await foreach (var work in works.AsAsyncEnumerable().WithCancellation(token))
        {
            Bound(work.MemoryRecallReceiptJson);
            if (work.MemoryRecallReceiptJson is null) { unknown |= work.SourceType == "chat-turn"; continue; }
            MemoryRecallDispatchEvidence.ErasureRead read;
            try { read = MemoryRecallDispatchEvidence.InspectErasureRead(work.MemoryRecallReceiptJson, true); }
            catch (InvalidOperationException) { unknown = true; continue; }
            if (read.Binding!.OrganizationId != organization || read.Binding.InstallationId != work.AgentInstallationId ||
                read.PayloadHash != work.PayloadHash || work.SourceType != "chat-turn" || work.SourceId != read.Binding.TurnId.ToString("D"))
            { unknown = true; continue; }
            queued.Add(new(work.Id, work.AgentInstallationId, read));
            if (read.References.Any(affected.Contains)) workIds.Add(work.Id);
        }
        var receipts = db.AgentMemoryReadReceipts.AsNoTracking().Where(x => x.OrganizationId == organization).OrderBy(x => x.Id).Take(8193);
        await foreach (var receipt in receipts.AsAsyncEnumerable().WithCancellation(token))
        {
            Bound(receipt.EvidenceJson);
            MemoryRecallDispatchEvidence.ErasureRead read;
            try { read = MemoryRecallDispatchEvidence.InspectErasureRead(receipt.EvidenceJson, receipt.Capability == MemoryRecallDispatchEvidence.QueuedRecallCapability); }
            catch (InvalidOperationException) { unknown = true; continue; }
            if (receipt.RuntimeId == Guid.Empty || receipt.WorkId == Guid.Empty || receipt.EmployeeId == Guid.Empty || receipt.InstallationId == Guid.Empty ||
                read.Binding is { } binding && (binding.OrganizationId != organization || binding.InstallationId != receipt.InstallationId ||
                    binding.EmployeeId != receipt.EmployeeId || binding.AuthorityHash != receipt.AuthorityHash))
            { unknown = true; continue; }
            delivered.Add(new(receipt.RuntimeId, receipt.WorkId, receipt.EmployeeId, receipt.InstallationId, read));
            if (read.References.Any(affected.Contains)) runtimeIds.Add(receipt.RuntimeId);
        }
        // Legacy runtimes could have read the source without recording it. No inventory
        // may claim their retained work/context is unrelated based on absent receipts.
        unknown |= await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.AgentInstallation!.BusinessId == org &&
            x.MemoryReadEvidenceVersion != CSweet.Domain.Setup.AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion && db.AgentWorkAttempts.Any(a => a.RuntimeInstanceId == x.Id), token);
        if (unknown) throw new InvalidOperationException("memory_erasure_work_lineage_review_required");
        return new(queued, delivered, workIds.Order().ToArray(), runtimeIds.Order().ToArray());
    }
}
