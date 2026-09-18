using CSweet.Domain.Analytics;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

/// <summary>Replays a database-only operation after a competing execution projection wins.</summary>
public static class WorkExecutionConflictRetry
{
    public static async Task<T> RunAsync<T>(CSweetDbContext db, Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        // Never discard a caller's pending changes or replay part of its transaction.
        // Each operation must perform its reads/authorization again and commit atomically.
        var canRetry = db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null &&
            !db.ChangeTracker.HasChanges();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await operation(); }
            catch (DbUpdateConcurrencyException exception) when (canRetry && attempt < 3 &&
                db.Database.CurrentTransaction is null && exception.Entries.Count > 0 &&
                exception.Entries.All(x => x.Entity is WorkExecutionContext or WorkExecutionInterval))
            {
                // SaveChanges rolled back the source mutation, intervals and outbox together.
                // Remove all failed-save additions too, rather than reusing their stale revisions.
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    public static Task RunAsync(CSweetDbContext db, Func<Task> operation, CancellationToken cancellationToken) =>
        RunAsync(db, async () => { await operation(); return true; }, cancellationToken);
}
