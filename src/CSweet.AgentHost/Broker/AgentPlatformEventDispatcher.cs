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
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        AgentDispatchLoop.RunAsync(DispatchPendingAsync, clock, logger, stoppingToken);

    internal async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<Guid> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            pending = await db.AgentPlatformEventOutbox.AsNoTracking()
                .Where(x => x.Status == AgentPlatformEventOutboxStatus.Pending && x.NextAttemptAt <= now)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                .Select(x => x.Id).Take(50).ToListAsync(cancellationToken);
        }

        foreach (var eventId in pending)
        {
            try
            {
                // Inbox.EnqueueAsync saves through this scope. A failed insert must never
                // leave tracked entities poisoning another event or its failure receipt.
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
                var item = await db.AgentPlatformEventOutbox.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
                if (item is null || item.Status != AgentPlatformEventOutboxStatus.Pending || item.NextAttemptAt > now)
                    continue;
                var router = scope.ServiceProvider.GetRequiredService<AgentWorkRouter>();
                var runtimeManager = scope.ServiceProvider.GetRequiredService<IAgentRuntimeManager>();
                var audit = scope.ServiceProvider.GetService<IAuditEventWriter>();
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
                        CSweet.WorkManagement.Contracts.PersonalTodoEvents.Available or CSweet.Infrastructure.WorkManagement.WorkItemDiscussion.Changed or
                        DelegationCapabilities.Received or DelegationCapabilities.Changed or DelegationCapabilities.Execution
                        ? new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
                        : now.AddHours(1),
                    cancellationToken: cancellationToken);
                var deliveries = routing.DeliveryCount;
                foreach (var recipientInstallationId in routing.RecipientInstallationIds)
                {
                    try
                    {
                        await runtimeManager.EnsureRuntimeQueuedAsync(
                            recipientInstallationId,
                            $"Received platform event {item.EventType} ({item.Id:D}).",
                            interactive: true,
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // Inbox persistence is the delivery boundary. Capacity and other temporary
                        // runtime-start conditions are reconciled independently from durable delivery.
                        logger.LogInformation(exception,
                            "Delivered platform event {EventId} to installation {InstallationId}; runtime activation remains pending.",
                            item.Id,
                            recipientInstallationId);
                    }
                }
                if (item.EventType is PersonalTodoEvents.Available or CSweet.Infrastructure.WorkManagement.WorkItemDiscussion.Changed or
                    DelegationCapabilities.Received or DelegationCapabilities.Changed or DelegationCapabilities.Execution && deliveries == 0)
                {
                    item.Attempts++;
                    item.NextAttemptAt = now.AddSeconds(30);
                    item.LastError = "The target installation is unavailable or has not granted the required event subscription.";
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
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
                item.LastError = deliveries == 0 && item.EventType is CSweet.Agent.SDK.ProjectHealthEvents.ReviewDue or CSweet.Agent.SDK.ProjectHealthEvents.IncidentChanged or CSweet.Agent.SDK.ProjectHealthEvents.OwnerReportRequested
                    ? "Incident recipient is unavailable or has no approved event subscription; the platform handoff deadline remains active."
                    : null;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Could not publish platform event {EventId}.", eventId);
                try
                {
                    await RecordFailureAsync(eventId, exception, cancellationToken);
                }
                catch (Exception receiptException) when (
                    receiptException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // A receipt/database outage must not prevent trying the other selected events.
                    // The unchanged durable event remains eligible on the next pass.
                    logger.LogError(receiptException, "Could not record delivery failure for platform event {EventId}.", eventId);
                }
            }
        }
    }

    private async Task RecordFailureAsync(Guid eventId, Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var item = await db.AgentPlatformEventOutbox.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        if (item is null || item.Status != AgentPlatformEventOutboxStatus.Pending) return;
        item.Attempts++;
        item.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, item.Attempts)));
        item.LastError = exception.Message.Length <= 2048 ? exception.Message : exception.Message[..2048];
        if (item.Attempts >= 12) item.Status = AgentPlatformEventOutboxStatus.Failed;
        await db.SaveChangesAsync(cancellationToken);
        if (item.EventType == ResourceChangeEvents.Requested &&
            scope.ServiceProvider.GetService<IAuditEventWriter>() is { } audit)
            await audit.WriteAsync("management.resource-change.delivery-retry", nameof(AgentPlatformEventOutboxItem),
                item.Id, $"Resource-change delivery attempt {item.Attempts} failed.", cancellationToken: cancellationToken);
    }
}
