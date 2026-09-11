using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class WebPreviewMaintenanceWorker(IServiceScopeFactory scopes, ILogger<WebPreviewMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); deadline.CancelAfter(TimeSpan.FromSeconds(25));
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebPreviewExecutionService>().MaintainAsync(deadline.Token);
                await scope.ServiceProvider.GetRequiredService<WebPreviewTriageService>().DispatchAsync(scope.ServiceProvider.GetRequiredService<AgentWorkInbox>(), deadline.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { logger.LogWarning("Web preview maintenance will retry ({ErrorType}).", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public sealed partial class WebPreviewExecutionService
{
    public async Task MaintainAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var hostIds = await db.WebPreviewJobs.Where(x => x.TeardownConfirmedAt == null && x.WebHostId != null)
            .Select(x => x.WebHostId).Distinct().Take(128).ToListAsync(token);
        foreach (var id in hostIds)
        {
            var host = await db.WebHostRegistrations.SingleAsync(x => x.Id == id, token);
            await ReconcileQueueAsync(host, token);
            host.Revision++; // Serialize maintenance against delivery and heartbeat across replicas.
            await db.SaveChangesAsync(token);
        }
        db.WebPreviewEvidence.RemoveRange(await db.WebPreviewEvidence.Where(x => x.RetainUntil <= now).Take(512).ToListAsync(token));
        db.WebPreviewBrowserSessions.RemoveRange(await db.WebPreviewBrowserSessions.Where(x => x.ExpiresAt <= now).Take(512).ToListAsync(token));
        foreach (var finding in await db.WebPreviewFindings.Where(x => x.RetainUntil <= now && x.EvidenceJson != "{}").Take(512).ToListAsync(token))
        { finding.EvidenceJson = "{}"; finding.Revision++; }
        foreach (var completed in await db.WebHostCommands.Where(x => (x.Action != "http" && x.Action != "test" || x.Action == "test" && x.CreatedAt <= now.AddDays(-7)) && x.Status == "Completed" && x.ResponseJson != null).Take(256).ToListAsync(token))
        { completed.ResponseJson = null; completed.Revision++; }
        foreach (var expiredTest in await db.WebHostCommands.Where(x => x.Action == "test" && x.CreatedAt <= now.AddDays(-7) && x.BodyJson != "{}").Take(256).ToListAsync(token))
        { expiredTest.BodyJson = "{}"; expiredTest.ResponseJson = null; expiredTest.Revision++; if (expiredTest.Status == "Pending") expiredTest.Status = "Cancelled"; }
        // HTTP payloads are transport data, never telemetry. Keep only acknowledgement identity/digest.
        foreach (var command in await db.WebHostCommands.Where(x => x.Action == "http" && x.CreatedAt < now.AddMinutes(-2) &&
            (x.BodyJson != "{}" || x.ResponseJson != null)).Take(256).ToListAsync(token))
        {
            command.BodyJson = "{}"; command.ResponseJson = null; command.Revision++;
            if (command.Status == "Pending") command.Status = "Cancelled";
        }
        await db.SaveChangesAsync(token);
    }
}
