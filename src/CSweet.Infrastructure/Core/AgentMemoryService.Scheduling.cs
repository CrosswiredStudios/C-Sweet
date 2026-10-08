using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private sealed class MemoryProviderBusyException : Exception;

    private async Task AcquireProviderLeaseAsync(Guid providerId, Guid jobId, Guid token, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now + EnrichmentLeaseDuration;
        if (db.Database.IsNpgsql())
        {
            var acquired = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MemoryEnrichmentProviderLeases" ("ProviderId", "JobId", "LeaseToken", "ExpiresAt")
                VALUES ({providerId}, {jobId}, {token}, {expires})
                ON CONFLICT ("ProviderId") DO UPDATE SET "JobId"=EXCLUDED."JobId", "LeaseToken"=EXCLUDED."LeaseToken", "ExpiresAt"=EXCLUDED."ExpiresAt"
                WHERE "MemoryEnrichmentProviderLeases"."ExpiresAt" <= {now}
                    OR NOT EXISTS (SELECT 1 FROM "MemoryCaptureOutbox" job
                        WHERE job."Id"="MemoryEnrichmentProviderLeases"."JobId"
                            AND job."LeaseToken"="MemoryEnrichmentProviderLeases"."LeaseToken"
                            AND job."Status"='Processing' AND job."LeaseExpiresAt">{now})
                """, cancellationToken);
            if (acquired == 0) throw new MemoryProviderBusyException();
            return;
        }
        var lease = await db.MemoryEnrichmentProviderLeases.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (lease is not null)
        {
            await db.Entry(lease).ReloadAsync(cancellationToken);
            if (lease.ExpiresAt > now) throw new MemoryProviderBusyException();
        }
        else db.MemoryEnrichmentProviderLeases.Add(lease = new() { ProviderId = providerId });
        lease.JobId = jobId; lease.LeaseToken = token; lease.ExpiresAt = expires;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReleaseProviderLeaseAsync(Guid providerId, Guid token)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            if (db.Database.IsNpgsql())
                await db.MemoryEnrichmentProviderLeases.Where(x => x.ProviderId == providerId && x.LeaseToken == token).ExecuteDeleteAsync(cleanup.Token);
            else
            {
                var lease = await db.MemoryEnrichmentProviderLeases.SingleOrDefaultAsync(x => x.ProviderId == providerId && x.LeaseToken == token, cleanup.Token);
                if (lease is not null) { db.Remove(lease); await db.SaveChangesAsync(cleanup.Token); }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Memory provider reservation cleanup failed ({FailureType}); the reservation will expire.", exception.GetType().Name);
        }
    }
}
