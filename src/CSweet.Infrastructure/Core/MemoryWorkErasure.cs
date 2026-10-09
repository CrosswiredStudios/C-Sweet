using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>
/// Internal work/runtime cleanup enlisted in an authorized erasure transaction. The caller
/// derives initial IDs from reviewed read evidence and authorizes the full returned closure.
/// Dispose after committing/rolling back: installation claim gates cover the entire transaction.
/// </summary>
internal sealed partial class MemoryWorkErasure(CSweetDbContext db, IDataProtectionProvider? protection = null, TimeProvider? clock = null,
    IDataProtectionProvider? diagnosticProtection = null) : IDisposable
{
    internal const string FailureCode = "memory.source_erased";
    internal const string SafeMessage = "This work's retained memory was erased. Its content is unavailable and it will not be replayed.";
    private const int MaximumWorks = 1024, MaximumAttempts = 4096, MaximumRuntimes = 128;
    private readonly List<SemaphoreSlim> gates = [];
    private readonly HashSet<Plan> plans = [];
    private Guid? transactionId;
    private bool disposed;

    internal sealed record Work(Guid Id, Guid InstallationId);
    internal sealed record Runtime(Guid Id, Guid InstallationId);
    internal sealed record ModelDiagnostics(IReadOnlyList<Guid> Runs, IReadOnlyDictionary<Guid, string> AuditEvidence);
    internal sealed class Plan(MemoryWorkErasure owner, Guid transaction, Guid organization,
        IReadOnlyList<Work> works, IReadOnlyList<Runtime> runtimes, ModelDiagnostics modelRuns)
    {
        internal MemoryWorkErasure Owner { get; } = owner;
        internal Guid Transaction { get; } = transaction;
        internal Guid Organization { get; } = organization;
        internal IReadOnlyList<Work> Works { get; } = works;
        internal IReadOnlyList<Runtime> Runtimes { get; } = runtimes;
        internal IReadOnlyList<Guid> ModelRuns { get; } = modelRuns.Runs;
        internal IReadOnlyDictionary<Guid, string> ModelAuditEvidence { get; } = modelRuns.AuditEvidence;
    }
    internal sealed record Result(int ClearedWorks, int DeletedProgress, int ResetRuntimes);

    internal async Task AcquireAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is not { } transaction || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Work erasure requires a clean caller-owned PostgreSQL transaction.");
        if (transactionId is { } previous && previous != transaction.TransactionId)
            throw new InvalidOperationException("Use a new work erasure scope for each transaction.");
        try
        {
            // All nonwaiting: a claimant may already own an advisory lock, or a broker
            // read may own the memory barrier. Fail/rollback rather than reversing either.
            await db.Database.ExecuteSqlRawAsync("""
                LOCK TABLE "AgentWorkItems", "AgentWorkAttempts", "AgentWorkProgress",
                    "AgentRuntimeInstances", "McpAgentSessions", "AgentMemoryReadReceipts", "AgentRunLogs",
                    "ComputeAuditOutbox", "AuditEvents", "AuditEventPayloads" IN EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "AgentInstallations" IN SHARE MODE NOWAIT;
                """, token);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("Agent work is changing. Refresh the erasure review.", error); }
        transactionId = transaction.TransactionId;
    }

    internal async Task<Plan> PrepareAsync(Guid organization, IReadOnlyCollection<Guid> initialWorkIds,
        IReadOnlyCollection<Guid> initialRuntimeIds, CancellationToken token)
    {
        var transaction = RequireTransaction();
        if (organization == Guid.Empty || initialWorkIds.Count > MaximumWorks || initialRuntimeIds.Count > MaximumRuntimes ||
            initialWorkIds.Concat(initialRuntimeIds).Any(x => x == Guid.Empty)) throw Limit();
        var workIds = initialWorkIds.ToHashSet(); var runtimeIds = initialRuntimeIds.ToHashSet();
        // Later work can echo earlier context; retries can carry that output to another
        // runtime. Review the whole connected work/runtime component, including history.
        var stable = false;
        for (var pass = 0; pass < 64; pass++)
        {
            var works = workIds.ToArray(); var runtimes = runtimeIds.ToArray();
            var links = await db.AgentWorkAttempts.AsNoTracking().Where(x => works.Contains(x.AgentWorkItemId) || runtimes.Contains(x.RuntimeInstanceId))
                .Select(x => new { x.AgentWorkItemId, x.RuntimeInstanceId }).Take(MaximumAttempts + 1).ToListAsync(token);
            if (links.Count > MaximumAttempts) throw Limit();
            var before = workIds.Count + runtimeIds.Count;
            foreach (var link in links) { workIds.Add(link.AgentWorkItemId); runtimeIds.Add(link.RuntimeInstanceId); }
            if (workIds.Count > MaximumWorks || runtimeIds.Count > MaximumRuntimes) throw Limit();
            if (before == workIds.Count + runtimeIds.Count) { stable = true; break; }
        }
        if (!stable) throw Limit();
        var selectedWorks = workIds.ToArray(); var selectedRuntimes = runtimeIds.ToArray();
        var workRows = await db.AgentWorkItems.AsNoTracking().Where(x => selectedWorks.Contains(x.Id))
            .Select(x => new { x.Id, x.AgentInstallationId, x.OrganizationId }).ToListAsync(token);
        var runtimeRows = await db.AgentRuntimeInstances.AsNoTracking().Where(x => selectedRuntimes.Contains(x.Id))
            .Select(x => new Runtime(x.Id, x.AgentInstallationId)).ToListAsync(token);
        var org = organization.ToString("D");
        if (workRows.Count != workIds.Count || runtimeRows.Count != runtimeIds.Count || workRows.Any(x => x.OrganizationId != org))
            throw new UnauthorizedAccessException();
        var installations = workRows.Select(x => x.AgentInstallationId).Concat(runtimeRows.Select(x => x.InstallationId)).Distinct().Order().ToArray();
        if (await db.AgentInstallations.CountAsync(x => installations.Contains(x.Id) && x.BusinessId == org, token) != installations.Length)
            throw new UnauthorizedAccessException();
        // Cross-installation attempt bindings are invalid, even inside one organization.
        if (await db.AgentWorkAttempts.AnyAsync(x => selectedWorks.Contains(x.AgentWorkItemId) &&
            x.AgentWorkItem!.AgentInstallationId != x.RuntimeInstance!.AgentInstallationId, token)) throw new UnauthorizedAccessException();
        if (await db.AgentWorkProgress.Where(x => selectedWorks.Contains(x.AgentWorkItemId)).Take(32769).CountAsync(token) > 32768) throw Limit();
        foreach (var installation in installations)
        {
            var gate = AgentWorkInbox.ClaimLocks.GetOrAdd(installation, _ => new SemaphoreSlim(1, 1));
            if (gates.Contains(gate)) continue;
            if (!await gate.WaitAsync(0, token)) throw new DbUpdateConcurrencyException("Work claim is in progress. Refresh the erasure review.");
            try
            {
                var key = $"agent-work-claim:{org}:{installation:D}";
                var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key},0)) AS \"Value\"").SingleAsync(token);
                if (!acquired) throw new DbUpdateConcurrencyException("Work claim is in progress. Refresh the erasure review.");
                gates.Add(gate);
            }
            catch { gate.Release(); throw; }
        }
        var modelRuns = await ReadModelDiagnosticsAsync(organization, selectedWorks, token);
        var plan = new Plan(this, transaction, organization,
            workRows.OrderBy(x => x.Id).Select(x => new Work(x.Id, x.AgentInstallationId)).ToList().AsReadOnly(),
            runtimeRows.OrderBy(x => x.Id).ToList().AsReadOnly(), modelRuns);
        plans.Add(plan); return plan;
    }

    internal async Task<Result> StageAsync(Plan plan, CancellationToken token)
    {
        if (protection is null) throw new InvalidOperationException("A preview-only work erasure scope cannot stage cleanup.");
        if (RequireTransaction() != plan.Transaction || !ReferenceEquals(plan.Owner, this) || !plans.Contains(plan))
            throw new InvalidOperationException("The work erasure plan belongs to another transaction.");
        if (db.ChangeTracker.Entries().Any(x => x.Entity is AgentWorkItem or AgentWorkAttempt or AgentWorkProgress or AgentRuntimeInstance or McpAgentSession or AgentRunLog))
            throw new InvalidOperationException("Work erasure requires untracked work/runtime rows.");
        var reset = new AgentMemoryRuntimeReset(db); var resetCount = 0;
        foreach (var runtime in plan.Runtimes)
        {
            var alreadyRequested = await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == runtime.Id && x.MemoryResetRequestedAt != null, token);
            if (await reset.StageErasureAsync(runtime.Id, runtime.InstallationId, plan.Organization.ToString("D"), token) && !alreadyRequested) resetCount++;
        }
        var result = await new AgentWorkInbox(db, protection, clock ?? TimeProvider.System).StageMemoryErasureAsync(plan.Works.Select(x => x.Id).ToArray(), token);
        await StageModelDiagnosticsAsync(plan.ModelAuditEvidence, token);
        // Reset/settlement and their outbox records have been saved, but the caller still
        // owns commit together with store/source erasure, authorization and replay audit.
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is AgentWorkItem or AgentWorkAttempt or AgentWorkProgress or AgentRuntimeInstance or McpAgentSession).ToArray())
            entry.State = EntityState.Detached;
        return new(result.Works, result.Progress, resetCount);
    }

    private Guid RequireTransaction()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return transactionId is { } id && db.Database.CurrentTransaction?.TransactionId == id ? id :
            throw new InvalidOperationException("Acquire work erasure locks in the current transaction first.");
    }
    private static InvalidOperationException Limit() => new("memory_erasure_scan_limit");
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var gate in gates.AsEnumerable().Reverse()) gate.Release();
    }
}
