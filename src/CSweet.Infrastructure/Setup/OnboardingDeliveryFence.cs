using System.Data;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Uses the inbox's lock order; callbacks inside this fence must not call Claim/Complete.</summary>
public static class OnboardingDeliveryFence
{
    public static async Task<T> RunAsync<T>(CSweetDbContext db, Guid organization, Guid installation,
        Func<Task<T>> action, CancellationToken ct)
    {
        var gate = AgentWorkInbox.ClaimLocks.GetOrAdd(installation, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // This boundary owns its unit of work. Reject caller edits before rollback cleanup
            // can detach anything; unrelated unsaved state must never be silently discarded.
            if (db.ChangeTracker.HasChanges())
                throw new InvalidOperationException("Persist pending changes before reviewing onboarding recovery.");
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            if (db.Database.IsNpgsql())
            {
                var key = $"agent-work-claim:{organization:D}:{installation:D}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
            }
            try
            {
                var result = await action();
                if (transaction is not null) await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
