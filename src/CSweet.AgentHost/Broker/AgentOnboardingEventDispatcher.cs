using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Communications;
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
        var pending = await db.AgentOnboardingEventOutbox
            .Where(x => x.Status == AgentOnboardingEventOutboxStatus.Pending &&
                        x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var item in pending)
        {
            var agent = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.Id == item.AgentOrganizationUserId &&
                            x.OrganizationId == item.OrganizationId)
                .Select(x => new { x.IsActive, x.AgentInstallationId })
                .SingleOrDefaultAsync(cancellationToken);
            if (agent is null || !agent.IsActive || !agent.AgentInstallationId.HasValue)
            {
                item.Status = AgentOnboardingEventOutboxStatus.Cancelled;
                item.LastError = "The agent employee is no longer active or installed.";
                continue;
            }

            try
            {
                var payload = CreatePayload(item);
                await router.EnqueueEventAsync(
                    item.OrganizationId.ToString("D"),
                    AgentLifecycleEvents.Onboarded,
                    JsonSerializer.SerializeToElement(payload, JsonOptions),
                    item.Id,
                    $"onboarding-event:{item.Id:N}",
                    agent.AgentInstallationId.Value,
                    requireSubscription: false,
                    deadline: now.AddHours(1),
                    cancellationToken);
                item.Attempts++;
                item.NextAttemptAt = now.AddSeconds(30);
                item.LastError = "Durable onboarding work is awaiting agent acknowledgement.";
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                item.Attempts++;
                item.NextAttemptAt = now.AddSeconds(Math.Min(60, Math.Pow(2, item.Attempts)));
                item.LastError = exception.Message;
                if (item.Attempts >= options.Value.MaximumAttempts)
                    item.Status = AgentOnboardingEventOutboxStatus.Failed;
                logger.LogWarning(exception, "Could not enqueue onboarding work {EventId}.", item.Id);
            }
        }
        if (pending.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    internal static AgentOnboardedEvent CreatePayload(AgentOnboardingEventOutboxItem item) =>
        new(
            item.OrganizationId,
            item.AgentOrganizationUserId,
            item.HiringOrganizationUserId,
            item.ConversationId,
            item.OccurredAt);
}
