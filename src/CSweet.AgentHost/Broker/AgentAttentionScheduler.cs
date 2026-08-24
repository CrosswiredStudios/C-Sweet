using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>Publishes targeted, durable attention reviews on the platform-owned schedule.</summary>
public sealed class AgentAttentionScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<AgentAttentionScheduler> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, clock);
        do
        {
            try
            {
                await DispatchDueReviewsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The agent attention scheduler iteration failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DispatchDueReviewsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var router = scope.ServiceProvider.GetRequiredService<AgentWorkRouter>();
        var now = clock.GetUtcNow();
        var schedules = await db.AgentSchedules
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .Where(x => x.IsEnabled && x.AgentInstallation!.IsEnabled &&
                x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var schedule in schedules)
        {
            if (!Subscribes(schedule.AgentInstallation!.Grant?.EventSubscriptionsJson))
                continue;

            var establishedAt = await db.AgentRuntimeInstances.AsNoTracking()
                .Where(x => x.AgentInstallationId == schedule.AgentInstallationId &&
                    x.Status == AgentRuntimeStatus.Running && x.McpSessionEstablishedAt != null)
                .OrderByDescending(x => x.McpSessionEstablishedAt)
                .Select(x => x.McpSessionEstablishedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (establishedAt is null)
                continue;

            var reconnected = schedule.LastAttentionReviewAt is null ||
                establishedAt > schedule.LastAttentionReviewAt;
            var periodic = schedule.NextAttentionReviewAt is null ||
                schedule.NextAttentionReviewAt <= now;
            var invalidated = !string.IsNullOrWhiteSpace(schedule.PendingAttentionReason);
            if (!reconnected && !periodic && !invalidated)
                continue;

            var reason = invalidated
                ? schedule.PendingAttentionReason!
                : schedule.LastAttentionReviewAt is null
                ? AgentAttentionReasons.Startup
                : reconnected
                    ? AgentAttentionReasons.Recovered
                    : AgentAttentionReasons.Periodic;
            var occurrence = invalidated
                ? schedule.AttentionInvalidatedAt ?? now
                : reconnected
                ? DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds())
                : DateTimeOffset.FromUnixTimeSeconds(
                    now.ToUnixTimeSeconds() / schedule.TickFrequencySeconds * schedule.TickFrequencySeconds);
            var nextReviewAt = occurrence.AddSeconds(schedule.TickFrequencySeconds);
            if (nextReviewAt <= now)
                nextReviewAt = now.AddSeconds(schedule.TickFrequencySeconds);
            var key = invalidated
                ? $"agent-attention:{schedule.AgentInstallationId:N}:state:{schedule.PendingAttentionCorrelationId?.ToString("N") ?? occurrence.ToUnixTimeMilliseconds().ToString()}"
                : reconnected
                ? $"agent-attention:{schedule.AgentInstallationId:N}:runtime:{establishedAt.Value.UtcTicks}"
                : $"agent-attention:{schedule.AgentInstallationId:N}:period:{occurrence.ToUnixTimeSeconds()}";
            var eventId = DeterministicEventId(key);
            var review = new AgentAttentionReviewDueEvent(eventId, occurrence, nextReviewAt, reason)
            {
                TriggerCategory = schedule.PendingAttentionTriggerCategory,
                CorrelationId = schedule.PendingAttentionCorrelationId
            };
            var delivered = await router.EnqueueEventAsync(
                schedule.AgentInstallation.BusinessId,
                AgentAttentionEvents.ReviewDue,
                JsonSerializer.SerializeToElement(review, JsonOptions),
                eventId,
                key,
                schedule.AgentInstallationId,
                requireSubscription: true,
                deadline: now.AddSeconds(Math.Max(schedule.TickFrequencySeconds * 2, 900)),
                cancellationToken: cancellationToken);
            if (delivered == 0)
                continue;

            // Advance from now; missed intervals never accumulate while an Office is offline.
            schedule.LastAttentionReviewAt = now;
            schedule.NextAttentionReviewAt = nextReviewAt;
            schedule.PendingAttentionReason = null;
            schedule.PendingAttentionTriggerCategory = null;
            schedule.PendingAttentionCorrelationId = null;
            schedule.AttentionInvalidatedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogDebug(
                "Queued {Reason} attention review for installation {InstallationId}; next review is {NextReviewAt}.",
                reason, schedule.AgentInstallationId, nextReviewAt);
        }
    }

    private static bool Subscribes(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json ?? "[]", JsonOptions) ?? [])
                .Contains(AgentAttentionEvents.ReviewDue, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Guid DeterministicEventId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}
