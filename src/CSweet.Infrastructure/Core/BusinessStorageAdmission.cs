using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Drain admitted storage writers before committing deletion intent.</summary>
public sealed class BusinessStorageAdmission(CSweetDbContext db, string key, bool exclusive, bool postgres) : IAsyncDisposable
{
    private int disposed;
    public static async Task<BusinessStorageAdmission> EnterAsync(CSweetDbContext db, Guid business,
        CancellationToken token, bool deleting = false, bool allowMissing = false)
    {
        var key = "csweet-storage:" + business.ToString("N");
        var postgres = db.Database.IsNpgsql();
        if (postgres)
        {
            await db.Database.OpenConnectionAsync(token);
            try
            {
                if (deleting) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({key}, 0))", token);
                else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock_shared(hashtextextended({key}, 0))", token);
            }
            catch { await db.Database.CloseConnectionAsync(); throw; }
        }
        var lease = new BusinessStorageAdmission(db, key, deleting, postgres);
        try
        {
            if (!deleting)
            {
                var status = await db.CoreOrganizations.AsNoTracking().Where(x => x.Id == business)
                    .Select(x => (OrganizationStatus?)x.Status).SingleOrDefaultAsync(token);
                if (status is OrganizationStatus.Deleting or OrganizationStatus.DeletionFailed ||
                    status is null && (!allowMissing || await db.ResourceCleanupRequests.AnyAsync(x =>
                        x.BusinessId == business && x.Kind == CSweet.Infrastructure.Setup.ResourceCleanupQueue.BusinessStorage, token)))
                    throw new InvalidOperationException("Business storage is unavailable while deletion is pending.");
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (!postgres) return;
        try
        {
            if (exclusive) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({key}, 0))", CancellationToken.None);
            else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock_shared(hashtextextended({key}, 0))", CancellationToken.None);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
