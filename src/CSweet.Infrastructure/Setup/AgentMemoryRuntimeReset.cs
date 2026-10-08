using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Internal server-owned reset request, persisted with session fencing and a runtime event.</summary>
public sealed class AgentMemoryRuntimeReset(CSweetDbContext db)
{
    public async Task<bool> RequestAsync(Guid runtimeId, Guid tickId, Guid installationId, string organizationId,
        long grantRevision, string reasonCode, CancellationToken token, string? validationDiagnostic = null)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("A reset request must own its transaction or be staged by the queue.");
        if (db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("A reset request cannot commit unrelated pending changes.");
        var gate = AgentWorkInbox.ClaimLocks.GetOrAdd(installationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            for (var retry = 0; retry < 3; retry++)
            {
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
                if (db.Database.IsNpgsql())
                {
                    var key = $"agent-work-claim:{organizationId}:{installationId:D}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", token);
                }
                try
                {
                    var accepted = await StageAsync(runtimeId, tickId, installationId, organizationId, grantRevision, reasonCode, token, validationDiagnostic);
                    await db.SaveChangesAsync(token);
                    if (transaction is not null) await transaction.CommitAsync(token);
                    return accepted;
                }
                catch (DbUpdateConcurrencyException) when (retry < 2)
                {
                    if (transaction is not null) await transaction.RollbackAsync(token);
                    db.ChangeTracker.Clear();
                }
                catch
                {
                    // SaveChanges accepts tracked values before CommitAsync. After an
                    // uncertain/failed commit, a retry must read durable state again.
                    db.ChangeTracker.Clear();
                    throw;
                }
            }
            return false;
        }
        finally { gate.Release(); }
    }

    // Caller owns the installation claim lock and transaction, and saves/commits before returning.
    internal async Task<bool> StageAsync(Guid runtimeId, Guid tickId, Guid installationId, string organizationId,
        long grantRevision, string reasonCode, CancellationToken token, string? validationDiagnostic = null)
    {
        if (reasonCode is not (MemoryRuntimeResetRequiredException.LegacyEvidence or
            MemoryRuntimeResetRequiredException.RetainedEvidence or MemoryRuntimeResetRequiredException.ReceiptCapacity))
            throw new ArgumentException("Unknown server reset reason.", nameof(reasonCode));
        if (validationDiagnostic is not null && !System.Text.RegularExpressions.Regex.IsMatch(
                validationDiagnostic, @"\A[a-zA-Z0-9_.;=:\-]{1,512}\z"))
            throw new ArgumentException("Reset diagnostics must contain bounded codes and identifiers only.", nameof(validationDiagnostic));
        var runtime = await db.AgentRuntimeInstances.SingleOrDefaultAsync(x => x.Id == runtimeId &&
            x.TickId == tickId && x.AgentInstallationId == installationId, token);
        if (runtime is null || !AgentRuntimeInstance.IsActive(runtime.Status) ||
            !await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId && x.BusinessId == organizationId &&
                x.Grant != null && x.Grant.GrantRevision == grantRevision, token)) return false;
        return await StageCoreAsync(runtime, reasonCode, token, validationDiagnostic);
    }

    // The erasure coordinator owns current human authority, all affected installation
    // claim locks and the transaction. Revoked grants must not prevent a required reset.
    internal async Task<bool> StageErasureAsync(Guid runtimeId, Guid installationId, string organizationId, CancellationToken token)
    {
        var runtime = await db.AgentRuntimeInstances.SingleAsync(x => x.Id == runtimeId && x.AgentInstallationId == installationId, token);
        if (!await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId && x.BusinessId == organizationId, token))
            throw new UnauthorizedAccessException();
        return AgentRuntimeInstance.IsActive(runtime.Status) &&
            await StageCoreAsync(runtime, MemoryRuntimeResetRequiredException.ErasedEvidence, token);
    }

    private async Task<bool> StageCoreAsync(AgentRuntimeInstance runtime, string reasonCode, CancellationToken token, string? validationDiagnostic = null)
    {
        if (runtime.MemoryResetRequestedAt is not null) return true;
        var now = DateTimeOffset.UtcNow;
        runtime.MemoryResetRequestedAt = now;
        runtime.MemoryResetReasonCode = reasonCode;
        var attempts = await db.AgentWorkAttempts.Where(x => x.RuntimeInstanceId == runtime.Id && x.FinishedAt == null)
            .Take(1001).ToListAsync(token);
        if (attempts.Count > 1000) throw new InvalidOperationException("Runtime reset lease fencing exceeded its bound.");
        foreach (var attempt in attempts)
        {
            // Fence in the request transaction. The reconciler later writes the protected
            // result and coordination notifications; a late agent result cannot win in between.
            attempt.FinishedAt = now;
            attempt.Error = MemoryRuntimeResetRequiredException.FailureCode;
        }
        db.AgentRuntimeEvents.Add(new AgentRuntimeEvent
        {
            Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id, Status = runtime.Status,
            Reason = validationDiagnostic is null ? MemoryRuntimeResetRequiredException.FailureCode :
                $"{MemoryRuntimeResetRequiredException.FailureCode};reason={reasonCode};{validationDiagnostic}", OccurredAt = now
        });
        var sessions = await db.McpAgentSessions.Where(x => x.RuntimeInstanceId == runtime.Id && x.RevokedAt == null).Take(1025).ToListAsync(token);
        if (sessions.Count > 1024) throw new InvalidOperationException("Runtime reset session fencing exceeded its bound.");
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = MemoryRuntimeResetRequiredException.FailureCode;
        }
        return true;
    }
}
