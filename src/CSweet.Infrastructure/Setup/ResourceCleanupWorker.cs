using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

/// <summary>Bounded recovery of the deletion outbox, including missed wakes and host restarts.</summary>
public sealed class ResourceCleanupProcessor(CSweetDbContext db, InstallationRuntimeCleanup runtimes,
    BusinessStorageCleanup storage, ILogger<ResourceCleanupProcessor> logger)
{
    public async Task RunOnceAsync(CancellationToken token = default)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = await db.ResourceCleanupRequests.AsNoTracking()
            .Where(x => (x.Kind == ResourceCleanupQueue.Runtime || x.Kind == ResourceCleanupQueue.BusinessStorage) &&
                x.CompletedAt == null && x.AvailableAt <= now && (x.LeaseUntil == null || x.LeaseUntil < now))
            .OrderBy(x => x.AvailableAt).ThenBy(x => x.Id).Take(20).Select(x => x.Id).ToListAsync(token);
        foreach (var id in ids)
        {
            var lease = Guid.NewGuid();
            var until = DateTimeOffset.UtcNow.AddMinutes(3);
            if (db.Database.IsRelational())
            {
                if (await db.ResourceCleanupRequests.Where(x => x.Id == id && x.CompletedAt == null &&
                    (x.LeaseUntil == null || x.LeaseUntil < now)).ExecuteUpdateAsync(s =>
                        s.SetProperty(x => x.LeaseId, lease).SetProperty(x => x.LeaseUntil, until), token) == 0) continue;
            }
            var job = await db.ResourceCleanupRequests.SingleAsync(x => x.Id == id, token);
            if (db.Database.IsRelational()) await db.Entry(job).ReloadAsync(token);
            job.LeaseId = lease; job.LeaseUntil = until; job.Attempts++;
            await db.SaveChangesAsync(token);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            bool complete = false;
            string? error = null;
            try
            {
                complete = job.Kind switch
                {
                    ResourceCleanupQueue.Runtime => await runtimes.TryCleanupAsync(job.ResourceId, budget.Token),
                    ResourceCleanupQueue.BusinessStorage => await storage.TryCleanupAsync(job.ResourceId, budget.Token),
                    _ => throw new InvalidOperationException("Unknown host cleanup request.")
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Resource cleanup {CleanupId} will retry.", id);
                error = exception.GetType().Name;
            }
            // Cleanup effects are idempotent. A lost lease may never acknowledge another worker's attempt.
            var completedAt = complete ? (DateTimeOffset?)DateTimeOffset.UtcNow : null;
            var next = DateTimeOffset.UtcNow.AddSeconds(error is null ? 3 : Math.Min(300, Math.Pow(2, Math.Min(job.Attempts, 8))));
            if (db.Database.IsRelational())
                await db.ResourceCleanupRequests.Where(x => x.Id == id && x.LeaseId == lease).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.CompletedAt, completedAt).SetProperty(x => x.AvailableAt, next)
                        .SetProperty(x => x.LastError, error).SetProperty(x => x.Attempts, job.Attempts)
                        .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), token);
            else
            {
                job.CompletedAt = completedAt; job.AvailableAt = next; job.LastError = error;
                job.LeaseId = null; job.LeaseUntil = null;
                await db.SaveChangesAsync(token);
            }
            db.ChangeTracker.Clear();
        }
    }
}

public sealed class ResourceCleanupWorker(IServiceScopeFactory scopes, ILogger<ResourceCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ResourceCleanupProcessor>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Deletion outbox recovery will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
