using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.WorkManagement.Contracts;
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
        var runtimeManager = scope.ServiceProvider.GetRequiredService<IAgentRuntimeManager>();
        var audit = scope.ServiceProvider.GetService<IAuditEventWriter>();
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
                var routing = await router.EnqueueEventWithRecipientsAsync(
                    item.OrganizationId.ToString("D"),
                    item.EventType,
                    payload.RootElement.Clone(),
                    item.Id,
                    item.IdempotencyKey,
                    item.TargetInstallationId,
                    requireSubscription: true,
                    deadline: item.EventType is WorkItemEvents.Assigned or
                        CSweet.WorkManagement.Contracts.PersonalTodoEvents.Available
                        ? new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
                        : now.AddHours(1),
                    cancellationToken: cancellationToken);
                var deliveries = routing.DeliveryCount;
                foreach (var recipientInstallationId in routing.RecipientInstallationIds)
                {
                    await runtimeManager.EnsureRuntimeQueuedAsync(
                        recipientInstallationId,
                        $"Received platform event {item.EventType} ({item.Id:D}).",
                        cancellationToken: cancellationToken);
                }
                if (item.EventType == ResourceChangeEvents.Requested &&
                    payload.RootElement.TryGetProperty("requestId", out var requestIdElement) &&
                    requestIdElement.TryGetGuid(out var requestId))
                {
                    var resourceChange = await db.ResourceChangeRequests
                        .SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);
                    if (resourceChange is not null)
                    {
                        resourceChange.DeliveryStatus = deliveries > 0 ? "Delivered" : "UnsupportedByManager";
                        resourceChange.UpdatedAt = now;
                        if (audit is not null)
                            await audit.WriteAsync(
                                deliveries > 0
                                    ? "management.resource-change.delivered"
                                    : "management.resource-change.unsupported-manager",
                                nameof(ResourceChangeRequestRecord),
                                resourceChange.Id,
                                deliveries > 0
                                    ? "Delivered the resource-change request to the manager installation."
                                    : "The manager installation does not subscribe to resource-change requests.",
                                cancellationToken: cancellationToken);
                    }
                }
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
                if (audit is not null && item.EventType == ResourceChangeEvents.Requested)
                    await audit.WriteAsync(
                        "management.resource-change.delivery-retry",
                        nameof(AgentPlatformEventOutboxItem),
                        item.Id,
                        $"Resource-change delivery attempt {item.Attempts} failed.",
                        cancellationToken: cancellationToken);
                logger.LogWarning(exception, "Could not publish platform event {EventId}.", item.Id);
            }
        }
        if (pending.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }
}
