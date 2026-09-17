using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Analytics;

/// <summary>Closes abandoned measurement intervals without extending execution through downtime.</summary>
public sealed class WorkExecutionRecoveryWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<WorkExecutionRecoveryWorker> logger) : BackgroundService
{
    public static async Task<int> RecoverAsync(CSweetDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.WorkExecutionIntervals.Where(x => x.EndedAt == null && db.AgentWorkAttempts.Any(a =>
                a.Id == x.AgentWorkAttemptId && (a.LeaseExpiresAt <= now || a.FinishedAt != null)))
            .OrderBy(x => x.StartedAt).Take(256).ToListAsync(ct);
        var closed = 0;
        foreach (var interval in rows)
        {
            var context = await db.WorkExecutionContexts.FindAsync([interval.AgentWorkAttemptId], ct);
            var attempt = await db.AgentWorkAttempts.AsNoTracking().SingleAsync(x => x.Id == interval.AgentWorkAttemptId, ct);
            if (attempt.FinishedAt is null && attempt.LeaseExpiresAt > now) continue;
            // ConfirmedThrough can include authenticated task-focus transitions after the last
            // heartbeat. Never substitute the recovery worker's current time.
            interval.EndedAt = interval.ConfirmedThrough;
            interval.EndReason = attempt.LeaseExpiresAt <= now || attempt.Error == "lease_expired" ? "LeaseLost" : "AttemptEnded";
            closed++;
            if (context is not null) { context.WorkItemId = null; context.Revision++; }
        }
        if (closed > 0) await db.SaveChangesAsync(ct);
        return closed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RecoverAsync(scope.ServiceProvider.GetRequiredService<CSweetDbContext>(), clock.GetUtcNow(), stoppingToken);
                }
                catch (DbUpdateConcurrencyException) { /* A heartbeat/focus change won; retry fresh next pass. */ }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogWarning(error, "Execution timing recovery will retry from persisted evidence."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
