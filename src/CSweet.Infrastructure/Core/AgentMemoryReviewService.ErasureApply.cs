using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record PendingErasureRuntime(Guid Id, DateTimeOffset RequestedAt);
    private sealed record ErasureReceiptInventory(MemoryPartition[] Audiences, PendingErasureRuntime[] PendingRuntimes, Guid[] TurnIds,
        ErasureAudienceOwner[]? Owners = null, int OwnershipVersion = 0);

    public async Task<MemoryErasureResponse> EraseSourceAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, EraseMemorySourceRequest request, CancellationToken cancellationToken = default)
    {
        if (episodeId == Guid.Empty || request.OperationId == Guid.Empty || request.EvidenceToken is not { Length: 64 } ||
            !request.EvidenceToken.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid erasure review.");
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await RequireHoldAuthorityAsync(organizationId, employeeId, applicationUserId, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":erasure:" + request.OperationId.ToString("D")},0))", cancellationToken);
            var hash = Hash(new { organizationId, employeeId, episodeId, applicationUserId, actor, request });
            var receipt = await db.MemoryErasureReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            // Replay must work after the erased episode has disappeared, and still
            // require current authority to every audience saved in the immutable receipt.
            if (receipt is not null)
            {
                if (receipt.RequestHash != hash || receipt.EmployeeId != employeeId || receipt.EpisodeId != episodeId) throw Changed();
                var replay = await ErasureResponseAsync(receipt, applicationUserId, actor, true, cancellationToken);
                await transaction.CommitAsync(cancellationToken); return replay;
            }
            using var work = new MemoryWorkErasure(db, protection, clock);
            var capture = new MemoryCaptureErasure(db);
            await AcquireErasureBarriersAsync(work, capture, cancellationToken);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var plan = await PrepareErasureAsync(organizationId, employeeId, applicationUserId, actor, episodeId, store, work, capture, cancellationToken);
            if (plan.Impact.ApplyBlockedReason is { } blocked) throw new InvalidOperationException(blocked);
            if (plan.Impact.EvidenceToken != request.EvidenceToken || plan.Execution is null) throw Changed();
            var runtimeIds = plan.Execution.Work.Runtimes.Select(x => x.Id).ToArray();
            var active = (await db.AgentRuntimeInstances.AsNoTracking().Where(x => runtimeIds.Contains(x.Id)).Select(x => new { x.Id, x.Status }).ToArrayAsync(cancellationToken))
                .Where(x => AgentRuntimeInstance.IsActive(x.Status)).Select(x => x.Id).Order().ToArray();
            var now = clock.GetUtcNow();
            var captures = await capture.StageAsync(plan.Execution.Capture, now, cancellationToken);
            var episodeJobs = await plan.Execution.Episodes.Owner.StageAsync(plan.Execution.Episodes,cancellationToken);
            var workResult = await work.StageAsync(plan.Execution.Work, cancellationToken);
            var pendingRuntimes = await db.AgentRuntimeInstances.AsNoTracking().Where(x => active.Contains(x.Id))
                .OrderBy(x => x.Id).Select(x => new PendingErasureRuntime(x.Id, x.MemoryResetRequestedAt!.Value)).ToArrayAsync(cancellationToken);
            var erasedRecords = 0; var erasedRevisions = 0;
            var allowed = plan.Inventory.Targets.ToHashSet();
            foreach (var root in plan.Roots)
            {
                if (await store.GetEpisodeAsync(root.Partition, root.Id, cancellationToken) is null) continue;
                // Earlier roots change the store-wide digest. Recompute under the same
                // barrier; no newly affected identity may expand the approved closure.
                var current = await store.PreviewEpisodeErasureAsync(root.Partition, root.Id, cancellationToken);
                if (current.Targets.Any(x => !allowed.Contains(x)) || current.BlockedReason is not null) throw Changed();
                var erased = await store.EraseEpisodeAsync(root.Partition, root.Id, current.EvidenceToken, cancellationToken);
                erasedRecords += erased.ErasedRecords; erasedRevisions += erased.ErasedRevisions;
            }
            var turns = plan.Turns.Where(x => x.MemoryErasedAt is null).Select(x => x.Id).ToArray();
            await db.ChatTurnTraceEvents.Where(x => turns.Contains(x.ChatTurnId)).ExecuteDeleteAsync(cancellationToken);
            await db.ChatTurns.Where(x => turns.Contains(x.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PartialResponse, string.Empty).SetProperty(x => x.ErrorMessage, (string?)null)
                .SetProperty(x => x.ErrorCode, "memory.source_erased").SetProperty(x => x.MemoryErasedAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            await db.AgentRuntimeInstances.Where(x => runtimeIds.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.LogExcerpt, (string?)null), cancellationToken);
            var memoryIds = allowed.Select(x => x.Id).Distinct().ToArray();
            await db.AgentMemoryRecallUses.Where(x => x.OrganizationId == organizationId && memoryIds.Contains(x.MemoryId)).ExecuteDeleteAsync(cancellationToken);
            receipt = new MemoryErasureReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId,
                EpisodeId = episodeId, OperationId = request.OperationId, ActorApplicationUserId = applicationUserId,
                ActorOrganizationUserId = actor, RequestHash = hash, CreatedAt = now,
                InventoryJson = JsonSerializer.Serialize(new ErasureReceiptInventory(plan.Audiences.ToArray(), pendingRuntimes, plan.Turns.Select(x => x.Id).ToArray(),
                    plan.Execution.Owners.ToArray(), OwnershipVersion: 1), JsonOptions),
                ErasedRecords = erasedRecords, ErasedRevisions = erasedRevisions, ClearedJobs = captures.ClearedJobs+episodeJobs,
                ClearedWorks = workResult.ClearedWorks, ClearedDiagnosticTurns = turns.Length };
            db.MemoryErasureReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.source.erased.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryErasure", EntityId: episodeId, Summary: "A human reviewer erased a memory source and its verified retained copies.",
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken);
            var result = await ErasureResponseAsync(receipt, applicationUserId, actor, false, cancellationToken);
            await transaction.CommitAsync(cancellationToken); return result;
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<MemoryErasureResponse> GetErasureStatusAsync(Guid organizationId, Guid employeeId, Guid operationId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid erasure operation.");
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await RequireHoldAuthorityAsync(organizationId, employeeId, applicationUserId, cancellationToken);
        var receipt = await db.MemoryErasureReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.EmployeeId == employeeId && x.OperationId == operationId, cancellationToken) ?? throw new KeyNotFoundException();
        var result = await ErasureResponseAsync(receipt, applicationUserId, actor, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return result;
    }

    private async Task<MemoryErasureResponse> ErasureResponseAsync(MemoryErasureReceipt receipt, Guid user, Guid actor, bool replay, CancellationToken token)
    {
        if (receipt.ActorApplicationUserId != user || receipt.ActorOrganizationUserId != actor) throw new UnauthorizedAccessException();
        var inventory = ReadErasureReceiptInventory(receipt.InventoryJson);
        await AuthorizeErasureAudiencesAsync(receipt.OrganizationId, receipt.EmployeeId, user, actor, inventory.Audiences, token);
        if (inventory.Owners is not null)
            await AuthorizeErasureOwnersAsync(receipt.OrganizationId, user, actor, inventory.Owners, token);
        var runtimeIds = inventory.PendingRuntimes.Select(x => x.Id).ToArray();
        var current = await db.AgentRuntimeInstances.AsNoTracking().Where(x => runtimeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, x.MemoryResetCompletedAt }).ToArrayAsync(token);
        var stopped = inventory.PendingRuntimes.Where(x => current.Any(r => r.Id == x.Id && !AgentRuntimeInstance.IsActive(r.Status) &&
            r.MemoryResetCompletedAt >= x.RequestedAt)).Select(x => x.Id).ToArray();
        // A missing runtime row is not proof of shutdown. Keep its acknowledgement pending.
        var pending = runtimeIds.Except(stopped).Count();
        return new(receipt.Id, receipt.OperationId, receipt.EpisodeId, receipt.ErasedRecords, receipt.ErasedRevisions,
            receipt.ClearedJobs, receipt.ClearedWorks, receipt.ClearedDiagnosticTurns, pending,
            pending == 0 ? "completed" : "runtime-reset-pending", receipt.CreatedAt, replay);
    }
}
