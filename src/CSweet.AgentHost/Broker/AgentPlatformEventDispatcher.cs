using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>Moves durable platform lifecycle events into subscribed agent work inboxes.</summary>
public sealed class AgentPlatformEventDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<AgentPlatformEventDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        do { await DispatchPendingAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var router = scope.ServiceProvider.GetRequiredService<AgentWorkRouter>();
        var now = clock.GetUtcNow();
        var pending = await db.AgentPlatformEventOutbox
            .Where(x => x.Status == AgentPlatformEventOutboxStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var item in pending)
        {
            try
            {
                using var payload = JsonDocument.Parse(item.DataJson);
                await router.EnqueueEventAsync(
                    item.OrganizationId.ToString("D"),
                    item.EventType,
                    payload.RootElement.Clone(),
                    item.IdempotencyKey,
                    requireSubscription: true,
                    deadline: now.AddHours(1),
                    cancellationToken: cancellationToken);
                item.Status = AgentPlatformEventOutboxStatus.Published;
                item.PublishedAt = now;
                item.Attempts++;
                item.LastError = null;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                item.Attempts++;
                item.NextAttemptAt = now.AddSeconds(Math.Min(60, Math.Pow(2, item.Attempts)));
                item.LastError = exception.Message;
                if (item.Attempts >= 12) item.Status = AgentPlatformEventOutboxStatus.Failed;
                logger.LogWarning(exception, "Could not publish platform event {EventId}.", item.Id);
            }
        }
        if (pending.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }
}
