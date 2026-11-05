using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.AgentHost.Broker;

/// <summary>Moves lifecycle state into exact-installation durable work.</summary>
public sealed class AgentOnboardingEventDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<AgentOnboardingDeliveryOptions> options,
    ILogger<AgentOnboardingEventDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        AgentDispatchLoop.RunAsync(DispatchPendingAsync, clock, logger, stoppingToken);

    internal async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scan = scopeFactory.CreateAsyncScope();
        var scanDb = scan.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var now = clock.GetUtcNow();
        var pending = await scanDb.AgentOnboardingEventOutbox.AsNoTracking()
            .Where(x => (x.Status == AgentOnboardingEventOutboxStatus.Pending || x.Status == AgentOnboardingEventOutboxStatus.Failed) && x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt).Take(50)
            .Select(x => new { x.Id, x.OrganizationId, x.Status, x.Attempts, x.NextAttemptAt, Installation = scanDb.CoreOrganizationUsers.Where(a => a.Id == x.AgentOrganizationUserId).Select(a => a.AgentInstallationId).FirstOrDefault() })
            .ToArrayAsync(cancellationToken);
        foreach (var candidate in pending)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            try
            {
                await OnboardingDeliveryFence.RunAsync(db, candidate.OrganizationId, candidate.Installation ?? Guid.Empty, async () =>
                {
                    var item = await db.AgentOnboardingEventOutbox.SingleOrDefaultAsync(x => x.Id == candidate.Id, cancellationToken);
                    if (item is null || item.DeliveredAt is not null || item.Status is AgentOnboardingEventOutboxStatus.Delivered or AgentOnboardingEventOutboxStatus.Cancelled || item.NextAttemptAt > clock.GetUtcNow()) return false;
                    var agent = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == item.AgentOrganizationUserId && x.OrganizationId == item.OrganizationId)
                        .Select(x => new { x.IsActive, x.AgentInstallationId, PackageVersionId = (Guid?)x.AgentInstallation!.PackageVersionId }).SingleOrDefaultAsync(cancellationToken);
                    if (agent is null || !agent.IsActive || agent.AgentInstallationId is null || agent.PackageVersionId is null)
                    {
                        item.Status = AgentOnboardingEventOutboxStatus.Cancelled;
                        item.LastError = "The agent employee is no longer active or installed.";
                    }
                    else
                    {
                        if (agent.AgentInstallationId != candidate.Installation) return false; // Next scan takes the correct installation fence.
                        var deliveryKey = CreateDeliveryKey(item.Id, agent.PackageVersionId.Value);
                        var installationKey = $"{deliveryKey}:{agent.AgentInstallationId.Value:D}";
                        var delivery = await db.AgentWorkItems.SingleOrDefaultAsync(x => x.AgentInstallationId == agent.AgentInstallationId && x.IdempotencyKey == installationKey, cancellationToken);
                        if (delivery is not null)
                        {
                            var recovery = await OnboardingRecoveryBudget.FindAsync(db, delivery, cancellationToken);
                            if (recovery is not null) _ = OnboardingRecoveryBudget.Read(recovery, delivery);
                            ReconcileDelivery(item, delivery, clock.GetUtcNow(), options.Value.MaximumAttempts, recovery is not null);
                        }
                        else
                        {
                            var router = scope.ServiceProvider.GetRequiredService<AgentWorkRouter>();
                            await router.EnqueueEventAsync(item.OrganizationId.ToString("D"), AgentLifecycleEvents.Onboarded,
                                JsonSerializer.SerializeToElement(CreatePayload(item), JsonOptions), item.Id, deliveryKey,
                                agent.AgentInstallationId.Value, requireSubscription: false, deadline: clock.GetUtcNow().AddHours(1), cancellationToken);
                            item.Attempts++; item.Status = AgentOnboardingEventOutboxStatus.Pending;
                            item.NextAttemptAt = clock.GetUtcNow().AddSeconds(30);
                            item.LastError = "Durable onboarding work is awaiting agent acknowledgement.";
                        }
                    }
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The transaction rolls back both delivery and outbox. No raw provider/error text.
                logger.LogWarning("Could not reconcile onboarding event {EventId}: {ErrorType}.", candidate.Id, error.GetType().Name);
                await using var recoveryScope = scopeFactory.CreateAsyncScope();
                var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<CSweetDbContext>();
                await OnboardingDeliveryFence.RunAsync(recoveryDb, candidate.OrganizationId, candidate.Installation ?? Guid.Empty, async () =>
                {
                    var failed = await recoveryDb.AgentOnboardingEventOutbox.SingleOrDefaultAsync(x => x.Id == candidate.Id, cancellationToken);
                    if (failed is null || failed.DeliveredAt is not null || failed.Status != candidate.Status ||
                        failed.Status is AgentOnboardingEventOutboxStatus.Delivered or AgentOnboardingEventOutboxStatus.Cancelled ||
                        failed.Attempts != candidate.Attempts || failed.NextAttemptAt != candidate.NextAttemptAt) return false;
                    failed.Attempts = failed.Attempts == int.MaxValue ? int.MaxValue : failed.Attempts + 1;
                    failed.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(failed.Attempts, 8))));
                    failed.LastError = "Onboarding delivery could not be queued or reconciled.";
                    if (failed.Attempts >= options.Value.MaximumAttempts) failed.Status = AgentOnboardingEventOutboxStatus.Failed;
                    await recoveryDb.SaveChangesAsync(cancellationToken);
                    return true;
                }, cancellationToken);
            }
        }
    }

    internal static void ReconcileDelivery(AgentOnboardingEventOutboxItem item,
        AgentWorkItem delivery, DateTimeOffset now, int maximumAttempts, bool operatorRecovery = false)
    {
        var transientFailure = delivery.LastError?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) == true &&
            delivery.LastError.Split(';').Contains("retryable=true", StringComparer.Ordinal);
        if (!operatorRecovery && delivery.Status == AgentWorkStatus.DeadLetter && transientFailure &&
            delivery.AttemptCount < maximumAttempts)
        {
            // Continue the same work and source event; preserve attempt history and mutation keys.
            delivery.MaximumAttempts = maximumAttempts;
            delivery.Status = AgentWorkStatus.Pending;
            delivery.AvailableAt = now.AddSeconds(30);
            delivery.DeadlineAt = now.AddHours(1);
            item.Status = AgentOnboardingEventOutboxStatus.Pending;
            item.NextAttemptAt = delivery.AvailableAt;
            item.LastError = $"Retrying onboarding after a transient delivery failure: {delivery.LastError}";
            return;
        }
        if (delivery.Status is AgentWorkStatus.DeadLetter or
            AgentWorkStatus.Cancelled or AgentWorkStatus.Completed)
        {
            item.Status = AgentOnboardingEventOutboxStatus.Failed;
            item.LastError = delivery.Status == AgentWorkStatus.Completed
                ? "The agent completed its onboarding delivery without acknowledging the lifecycle event."
                : $"Onboarding delivery {delivery.Status} after {delivery.AttemptCount} attempts: {delivery.LastError}";
            // Retain the failure, but allow a later package version to receive a fresh delivery.
            item.NextAttemptAt = now.AddMinutes(5);
            return;
        }
        item.Status = AgentOnboardingEventOutboxStatus.Pending;
        item.NextAttemptAt = now.AddSeconds(30);
        item.LastError = "Durable onboarding work is awaiting agent acknowledgement.";
    }
    // A package that ignored onboarding may have completed its delivery without acknowledging
    // the lifecycle event. A new package gets one new delivery with the same source event ID.
    internal static string CreateDeliveryKey(Guid eventId, Guid packageVersionId) =>
        $"onboarding-event:{eventId:N}:package:{packageVersionId:N}";

    internal static AgentOnboardedEvent CreatePayload(AgentOnboardingEventOutboxItem item) =>
        new(
            item.OrganizationId,
            item.AgentOrganizationUserId,
            item.HiringOrganizationUserId,
            item.ConversationId,
            item.OccurredAt);
}
