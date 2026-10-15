using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;

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
        var scheduleIds = await db.AgentSchedules.AsNoTracking()
            .Where(x => x.IsEnabled && x.AgentInstallation!.IsEnabled &&
                x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active &&
                x.AgentInstallation.SetupState == PluginSetupState.Ready)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var scheduleId in scheduleIds)
        {
            try
            {
                await DispatchReviewAsync(scheduleId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // One unavailable installation must not block the rest of the roster.
                logger.LogWarning(exception, "Could not dispatch attention for schedule {ScheduleId}; it remains due.", scheduleId);
            }
        }
    }

    private async Task DispatchReviewAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var router = scope.ServiceProvider.GetRequiredService<AgentWorkRouter>();
        // The inbox insert and schedule advancement must commit together. Serialize replicas
        // before reading the schedule; NextAttentionReviewAt also fences concurrent invalidation.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (db.Database.IsNpgsql())
        {
            var lockKey = $"agent-attention:{scheduleId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        }

        var schedule = await db.AgentSchedules
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleOrDefaultAsync(x => x.Id == scheduleId && x.IsEnabled && x.AgentInstallation!.IsEnabled &&
                x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active &&
                x.AgentInstallation.SetupState == PluginSetupState.Ready, cancellationToken);
        if (schedule is null) return;
        var now = clock.GetUtcNow();

        if (!Subscribes(schedule.AgentInstallation!.Grant?.EventSubscriptionsJson))
            return;

        var establishedAt = await db.AgentRuntimeInstances.AsNoTracking()
            .Where(x => x.AgentInstallationId == schedule.AgentInstallationId &&
                x.Status == AgentRuntimeStatus.Running && x.McpSessionEstablishedAt != null)
            .OrderByDescending(x => x.McpSessionEstablishedAt)
            .Select(x => x.McpSessionEstablishedAt)
            .FirstOrDefaultAsync(cancellationToken);
        // OnDemand attention is itself durable demand. Scheduled installations still
        // defer to their run schedule; AlwaysOn startup remains the runtime manager's job.
        if (establishedAt is null && schedule.ActivationMode != ActivationMode.OnDemand)
            return;

        var startup = schedule.LastAttentionReviewAt is null;
        var reconnected = establishedAt.HasValue && (startup || establishedAt > schedule.LastAttentionReviewAt);
        var periodic = schedule.NextAttentionReviewAt is null ||
            schedule.NextAttentionReviewAt <= now;
        var invalidated = !string.IsNullOrWhiteSpace(schedule.PendingAttentionReason);
        if (!startup && !reconnected && !periodic && !invalidated)
            return;

        // Capacity shortages/offline Offices must not accumulate periodic callbacks.
        // Keep a newer invalidation on the schedule until the existing review settles.
        if (await db.AgentWorkItems.AsNoTracking().AnyAsync(work =>
                work.AgentInstallationId == schedule.AgentInstallationId &&
                work.Kind == AgentWorkKind.Event && work.Name == AgentAttentionEvents.ReviewDue &&
                ((work.Status == AgentWorkStatus.Pending && work.DeadlineAt > now) ||
                 (work.Status == AgentWorkStatus.Leased && (work.DeadlineAt > now ||
                     work.Attempts.Any(attempt => attempt.FinishedAt == null && attempt.LeaseExpiresAt > now)))), cancellationToken))
            return;

        var reason = invalidated
            ? schedule.PendingAttentionReason!
            : startup
            ? AgentAttentionReasons.Startup
            : reconnected
                ? AgentAttentionReasons.Recovered
                : AgentAttentionReasons.Periodic;
        var occurrence = invalidated
            ? schedule.AttentionInvalidatedAt ?? now
            : startup || reconnected
            ? DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds())
            : DateTimeOffset.FromUnixTimeSeconds(
                now.ToUnixTimeSeconds() / schedule.TickFrequencySeconds * schedule.TickFrequencySeconds);
        var nextReviewAt = occurrence.AddSeconds(schedule.TickFrequencySeconds);
        if (nextReviewAt <= now)
            nextReviewAt = now.AddSeconds(schedule.TickFrequencySeconds);
        var key = invalidated
            ? $"agent-attention:{schedule.AgentInstallationId:N}:state:{schedule.PendingAttentionCorrelationId?.ToString("N") ?? "uncorrelated"}:{occurrence.UtcTicks}"
            : reconnected
            ? $"agent-attention:{schedule.AgentInstallationId:N}:runtime:{establishedAt!.Value.UtcTicks}"
            : startup
            ? $"agent-attention:{schedule.AgentInstallationId:N}:startup:{occurrence.ToUnixTimeSeconds()}"
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
            return;

        // Advance from now; missed intervals never accumulate while an Office is offline.
        schedule.LastAttentionReviewAt = now;
        schedule.NextAttentionReviewAt = nextReviewAt;
        schedule.PendingAttentionReason = null;
        schedule.PendingAttentionTriggerCategory = null;
        schedule.PendingAttentionCorrelationId = null;
        schedule.AttentionInvalidatedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogDebug(
            "Queued {Reason} attention review for installation {InstallationId}; next review is {NextReviewAt}.",
            reason, schedule.AgentInstallationId, nextReviewAt);
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
