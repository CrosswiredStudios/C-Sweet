using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal static class HiringOrganizationLock
{
    // All recommendation mutations and chat suggestion creation share this transaction lock.
    public static async Task AcquireAsync(CSweetDbContext db, Guid organizationId, CancellationToken token)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString()}, 0))", token);
    }
}
