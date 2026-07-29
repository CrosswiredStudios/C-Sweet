using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>Drives management reviews from durable application state rather than model memory or process timers.</summary>
public sealed class ManagementReviewScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ManagementReviewScheduler> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            await DispatchDueReviewsAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DispatchDueReviewsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var router = scope.ServiceProvider.GetRequiredService<AgentWorkRouter>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditEventWriter>();
        var now = clock.GetUtcNow();
        var cycles = await db.ManagementCycles.Where(x => x.IsEnabled).ToListAsync(cancellationToken);
        var due = cycles.Where(x => x.NextReviewAt == null || x.NextReviewAt <= now).ToList();
        foreach (var cycle in due)
        {
            var periodStart = now.AddDays(-1);
            var reportingLines = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.OrganizationId == cycle.OrganizationId && x.IsActive && x.ReportsToOrganizationUserId != null)
                .Select(x => new { ReportId = x.Id, ManagerId = x.ReportsToOrganizationUserId!.Value })
                .ToListAsync(cancellationToken);
            var existingReports = await db.ManagementCheckInRequests.AsNoTracking()
                .Where(x => x.OrganizationId == cycle.OrganizationId && x.CreatedAt >= periodStart && x.Status == "Pending")
                .Select(x => x.RequestedFromOrganizationUserId)
                .ToListAsync(cancellationToken);
            foreach (var line in reportingLines.Where(x => !existingReports.Contains(x.ReportId)))
            {
                db.ManagementCheckInRequests.Add(new CSweet.Domain.Core.ManagementCheckInRequestRecord
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = cycle.OrganizationId,
                    ManagementCycleId = cycle.Id,
                    RequestedByOrganizationUserId = line.ManagerId,
                    RequestedFromOrganizationUserId = line.ReportId,
                    CheckInType = "ManagerRollup",
                    TopicsJson = "[\"outcomes\",\"blockers\",\"risks\",\"capacity\",\"resource-needs\"]",
                    CreatedAt = now,
                    DueAt = now.AddHours(2)
                });
            }
            var eventPayload = new ManagementReviewDueEvent(
                cycle.Id, "ManagerRollup", periodStart, now, now.AddHours(2), cycle.TimeZone);
            var count = await router.EnqueueEventAsync(
                cycle.OrganizationId.ToString("D"),
                ManagementEvents.ReviewDue,
                JsonSerializer.SerializeToElement(eventPayload, JsonOptions),
                Guid.NewGuid(),
                $"management-review:{cycle.Id:D}:{now.UtcTicks}",
                cancellationToken: cancellationToken);
            cycle.NextReviewAt = NextWeekdayReview(now, cycle);
            await audit.WriteAsync("management-review.due", nameof(CSweet.Domain.Core.ManagementCycle), cycle.Id,
                $"Management review became due and was offered to {count} connected agent(s).",
                JsonSerializer.Serialize(new { cycle.OrganizationId, count, cycle.NextReviewAt }, JsonOptions), cancellationToken);
            logger.LogInformation("Published management review {CycleId} to {AgentCount} connected agents.", cycle.Id, count);
        }
        foreach (var cycle in cycles.Where(x => IsWeeklyReviewDue(now, x)))
        {
            var weekStart = now.AddDays(-7);
            var alreadyCreated = await db.ManagementCheckInRequests.AsNoTracking().AnyAsync(
                x => x.OrganizationId == cycle.OrganizationId && x.CheckInType == "WeeklyLeadership" && x.CreatedAt >= weekStart,
                cancellationToken);
            if (alreadyCreated) continue;
            var chiefId = await db.LeadershipAssignments.AsNoTracking()
                .Where(x => x.OrganizationId == cycle.OrganizationId && x.PositionKey == "chief-of-staff" && x.EndsAt == null)
                .Select(x => (Guid?)x.OrganizationUserId).SingleOrDefaultAsync(cancellationToken);
            if (chiefId is null) continue;
            var leadershipIds = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.OrganizationId == cycle.OrganizationId && x.IsActive && x.Id != chiefId &&
                            (x.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Owner ||
                             x.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Manager))
                .Select(x => x.Id).ToListAsync(cancellationToken);
            var executiveSponsor = leadershipIds.FirstOrDefault();
            if (executiveSponsor == Guid.Empty) executiveSponsor = chiefId.Value;
            db.ManagementCheckInRequests.Add(new CSweet.Domain.Core.ManagementCheckInRequestRecord
            {
                Id = Guid.NewGuid(), OrganizationId = cycle.OrganizationId, ManagementCycleId = cycle.Id,
                RequestedByOrganizationUserId = executiveSponsor, RequestedFromOrganizationUserId = chiefId.Value,
                CheckInType = "WeeklyLeadership", TopicsJson = "[\"outcomes\",\"workstreams\",\"blockers\",\"decisions\",\"staffing\",\"budget\",\"forecast\",\"assumptions\"]",
                CreatedAt = now, DueAt = now.AddHours(2)
            });
            foreach (var leaderId in leadershipIds)
                db.ManagementCheckInRequests.Add(new CSweet.Domain.Core.ManagementCheckInRequestRecord
                {
                    Id = Guid.NewGuid(), OrganizationId = cycle.OrganizationId, ManagementCycleId = cycle.Id,
                    RequestedByOrganizationUserId = chiefId.Value, RequestedFromOrganizationUserId = leaderId,
                    CheckInType = "WeeklyLeadership", TopicsJson = "[\"outcomes\",\"blockers\",\"decisions\",\"staffing\",\"budget\"]",
                    CreatedAt = now, DueAt = now.AddHours(2)
                });
            var weeklyEvent = new ManagementReviewDueEvent(cycle.Id, "WeeklyLeadership", now.AddDays(-7), now, now.AddHours(2), cycle.TimeZone);
            var count = await router.EnqueueEventAsync(
                cycle.OrganizationId.ToString("D"),
                ManagementEvents.ReviewDue,
                JsonSerializer.SerializeToElement(weeklyEvent, JsonOptions),
                Guid.NewGuid(),
                $"weekly-management-review:{cycle.Id:D}:{now:yyyyMMdd}",
                cancellationToken: cancellationToken);
            await audit.WriteAsync("management-weekly-review.due", nameof(CSweet.Domain.Core.ManagementCycle), cycle.Id,
                $"Weekly leadership and workforce review became due and was offered to {count} connected agent(s).",
                cancellationToken: cancellationToken);
        }
        await QueueScheduledExecutiveBriefingsAsync(db, cycles, now, cancellationToken);
        await DispatchExecutiveBriefingsAsync(db, router, now, cancellationToken);
        await DispatchAgentManagerDeliveriesAsync(db, router, now, cancellationToken);
        var overdue = await db.ManagementCheckInRequests
            .Where(x => x.CheckInType != "ExecutiveBriefing" && x.Status == "Pending" && x.DueAt < now)
            .ToListAsync(cancellationToken);
        foreach (var request in overdue)
        {
            if (request.ReminderSentAt is null)
            {
                request.ReminderSentAt = now;
            }
            else if (request.DueAt <= now.AddDays(-1))
            {
                request.Status = "Stale";
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task QueueScheduledExecutiveBriefingsAsync(CSweetDbContext db,
        IReadOnlyList<CSweet.Domain.Core.ManagementCycle> cycles, DateTimeOffset now, CancellationToken token)
    {
        foreach (var cycle in cycles.Where(x => x.ExecutiveBriefingEnabled))
        {
            if (cycle.NextExecutiveBriefingAt is null)
            {
                cycle.NextExecutiveBriefingAt = ExecutiveBriefingScheduleCalculator.Next(now, cycle);
                continue;
            }
            if (cycle.NextExecutiveBriefingAt > now) continue;
            var scheduledAt = cycle.NextExecutiveBriefingAt.Value;
            var key = $"briefing:scheduled:{cycle.Id:D}:{scheduledAt.UtcTicks}";
            if (await db.ManagementCheckInRequests.AsNoTracking().AnyAsync(
                    x => x.OrganizationId == cycle.OrganizationId && x.IdempotencyKey == key, token))
            {
                cycle.NextExecutiveBriefingAt = ExecutiveBriefingScheduleCalculator.Next(now, cycle);
                continue;
            }
            var chief = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == cycle.OrganizationId && x.IsActive && x.AgentInstallationId != null &&
                    db.LeadershipAssignments.Any(a => a.OrganizationId == cycle.OrganizationId && a.OrganizationUserId == x.Id &&
                        a.PositionKey == "chief-of-staff" && a.EndsAt == null))
                .SingleOrDefaultAsync(token);
            if (chief is null) continue;
            var managerId = chief.ReportsToOrganizationUserId is { } configured && await db.CoreOrganizationUsers.AnyAsync(
                    x => x.Id == configured && x.OrganizationId == cycle.OrganizationId && x.IsActive, token)
                ? configured
                : await db.CoreOrganizationUsers.Where(x => x.OrganizationId == cycle.OrganizationId && x.IsActive &&
                        x.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Owner && x.Id != chief.Id)
                    .OrderBy(x => x.CreatedAt).Select(x => x.Id).FirstOrDefaultAsync(token);
            if (managerId == Guid.Empty) continue;
            db.ManagementCheckInRequests.Add(new CSweet.Domain.Core.ManagementCheckInRequestRecord
            {
                Id = Guid.NewGuid(), OrganizationId = cycle.OrganizationId, ManagementCycleId = cycle.Id,
                RequestedByOrganizationUserId = managerId, RequestedFromOrganizationUserId = chief.Id,
                CheckInType = "ExecutiveBriefing", TriggerType = "Scheduled", IdempotencyKey = key,
                TopicsJson = "[\"immediate-actions\",\"blockers\",\"decisions\",\"staffing\",\"deadlines\",\"budget\"]",
                CreatedAt = now, DueAt = now.AddHours(2)
            });
            cycle.NextExecutiveBriefingAt = ExecutiveBriefingScheduleCalculator.Next(now, cycle);
        }
        await db.SaveChangesAsync(token);
    }

    private async Task DispatchExecutiveBriefingsAsync(
        CSweetDbContext db,
        AgentWorkRouter router,
        DateTimeOffset now,
        CancellationToken token)
    {
        var requests = await db.ManagementCheckInRequests
            .Where(x => x.CheckInType == "ExecutiveBriefing" && (x.Status == "Pending" || x.Status == "AwaitingReport"))
            .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(token);
        foreach (var request in requests)
        {
            if (request.LastDispatchedAt > now.AddMinutes(-5)) continue;
            if (request.DispatchAttempts >= 3)
            {
                request.Status = "Failed";
                request.FailureCode ??= "agent_unavailable";
                request.FailureMessage ??= "The Chief did not return a briefing after three dispatch attempts.";
                continue;
            }
            var cycle = await db.ManagementCycles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ManagementCycleId, token);
            if (cycle is null) { request.Status = "Failed"; request.FailureCode = "cycle_not_found"; continue; }
            if (!string.Equals(request.TriggerType, "Manual", StringComparison.OrdinalIgnoreCase) &&
                ExecutiveBriefingScheduleCalculator.IsQuietHours(now, cycle)) continue;
            var installationId = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.Id == request.RequestedFromOrganizationUserId && x.OrganizationId == request.OrganizationId && x.IsActive)
                .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(token);
            if (!installationId.HasValue)
            {
                request.Status = "Failed"; request.FailureCode = "chief_unavailable";
                request.FailureMessage = "The active Chief employee has no runnable agent installation.";
                continue;
            }
            var due = CreateExecutiveBriefingDueEvent(request, cycle);
            var count = await router.EnqueueEventAsync(
                request.OrganizationId.ToString("D"),
                ManagementEvents.ReviewDue,
                JsonSerializer.SerializeToElement(due, JsonOptions),
                request.Id,
                ExecutiveBriefingDispatchKey(request.Id, request.DispatchAttempts + 1),
                installationId.Value,
                cancellationToken: token);
            request.DispatchAttempts++;
            request.LastDispatchedAt = now;
            if (count > 0)
            {
                request.Status = "AwaitingReport";
                request.FailureCode = null; request.FailureMessage = null;
            }
            else
            {
                request.Status = request.DispatchAttempts >= 3 ? "Failed" : "Pending";
                request.FailureCode = "agent_offline";
                request.FailureMessage = "The Chief is not connected or does not subscribe to management review events.";
            }
        }
        await db.SaveChangesAsync(token);
    }

    internal static ManagementReviewDueEvent CreateExecutiveBriefingDueEvent(
        CSweet.Domain.Core.ManagementCheckInRequestRecord request,
        CSweet.Domain.Core.ManagementCycle cycle) =>
        new(
            cycle.Id,
            "ExecutiveBriefing",
            request.CreatedAt.AddDays(-1),
            request.CreatedAt,
            request.DueAt,
            cycle.TimeZone)
        {
            RequestId = request.Id
        };

    internal static string ExecutiveBriefingDispatchKey(Guid requestId, int dispatchAttempt)
    {
        if (dispatchAttempt <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(dispatchAttempt), "Dispatch attempts are one-based.");
        return $"executive-briefing:{requestId:N}:dispatch:{dispatchAttempt}";
    }

    private async Task DispatchAgentManagerDeliveriesAsync(
        CSweetDbContext db,
        AgentWorkRouter router,
        DateTimeOffset now,
        CancellationToken token)
    {
        var deliveries = await db.ExecutiveBriefingDeliveries.Where(x => x.Channel == "AgentRuntime" && x.Status == "Pending" &&
                (x.LastAttemptAt == null || x.LastAttemptAt <= now.AddMinutes(-5)))
            .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(token);
        foreach (var delivery in deliveries)
        {
            if (delivery.Attempts >= 3)
            {
                delivery.Status = "Failed"; delivery.FailureCode = "manager_agent_unavailable";
                delivery.FailureMessage = "The managing agent did not connect after three delivery attempts.";
                var failedRequest = await db.ManagementCheckInRequests.SingleAsync(x => x.Id == delivery.ManagementCheckInRequestId, token);
                failedRequest.Status = "DeliveryFailed";
                continue;
            }
            var installationId = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.Id == delivery.RecipientOrganizationUserId && x.OrganizationId == delivery.OrganizationId && x.IsActive)
                .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(token);
            delivery.Attempts++; delivery.LastAttemptAt = now;
            if (!installationId.HasValue)
            {
                delivery.Status = "Failed"; delivery.FailureCode = "manager_agent_unavailable";
                delivery.FailureMessage = "The configured managing agent has no active installation.";
                var failedRequest = await db.ManagementCheckInRequests.SingleAsync(x => x.Id == delivery.ManagementCheckInRequestId, token);
                failedRequest.Status = "DeliveryFailed";
                continue;
            }
            var count = await router.EnqueueEventAsync(
                delivery.OrganizationId.ToString("D"),
                ManagementEvents.StatusReported,
                JsonDocument.Parse(delivery.PayloadJson).RootElement.Clone(),
                delivery.Id,
                delivery.Id.ToString("N"),
                installationId.Value,
                cancellationToken: token);
            if (count > 0)
            {
                delivery.Status = "Delivered"; delivery.DeliveredAt = now;
                var request = await db.ManagementCheckInRequests.SingleAsync(x => x.Id == delivery.ManagementCheckInRequestId, token);
                request.Status = "Delivered";
            }
            else if (delivery.Attempts >= 3)
            {
                delivery.Status = "Failed"; delivery.FailureCode = "manager_agent_offline";
                delivery.FailureMessage = "The managing agent is offline or does not subscribe to status reports.";
                var failedRequest = await db.ManagementCheckInRequests.SingleAsync(x => x.Id == delivery.ManagementCheckInRequestId, token);
                failedRequest.Status = "DeliveryFailed";
            }
        }
        await db.SaveChangesAsync(token);
    }

    private static DateTimeOffset NextWeekdayReview(DateTimeOffset now, CSweet.Domain.Core.ManagementCycle cycle)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(cycle.TimeZone); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        while (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) local = local.AddDays(1);
        var parts = cycle.DailyCheckInLocalTime.Split(':');
        local = local.AddHours(int.TryParse(parts.ElementAtOrDefault(0), out var hour) ? hour : 9)
            .AddMinutes(int.TryParse(parts.ElementAtOrDefault(1), out var minute) ? minute : 0);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool IsWeeklyReviewDue(DateTimeOffset now, CSweet.Domain.Core.ManagementCycle cycle)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(cycle.TimeZone); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTime(now, zone);
        if (!Enum.TryParse<DayOfWeek>(cycle.WeeklyReviewDay, true, out var reviewDay) || local.DayOfWeek != reviewDay) return false;
        var parts = cycle.WeeklyReviewLocalTime.Split(':');
        var hour = int.TryParse(parts.ElementAtOrDefault(0), out var parsedHour) ? parsedHour : 15;
        var minute = int.TryParse(parts.ElementAtOrDefault(1), out var parsedMinute) ? parsedMinute : 0;
        return local.TimeOfDay >= new TimeSpan(hour, minute, 0);
    }
}
