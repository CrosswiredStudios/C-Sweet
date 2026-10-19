using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

// Serialize short deletion advances, including owner retries and recovery on other
// API replicas. A session lock allows the purge to own its existing transaction.
internal sealed class OrganizationDeletionLock(CSweetDbContext db, bool postgres) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const long LockKey = 0x43535744454C;

    public static async Task<OrganizationDeletionLock> AcquireAsync(CSweetDbContext db, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        var postgres = db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        try
        {
            if (postgres)
            {
                await db.Database.OpenConnectionAsync(token);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({LockKey})", token);
            }
            return new(db, postgres);
        }
        catch
        {
            if (postgres) await db.Database.CloseConnectionAsync();
            Gate.Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (postgres)
            {
                try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None); }
                finally { await db.Database.CloseConnectionAsync(); }
            }
        }
        finally { Gate.Release(); }
    }
}
