using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ExecutiveBriefingService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    TimeProvider clock) : IExecutiveBriefingService
{
    public async Task<ExecutiveBriefingSettingsResponse?> GetSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var chief = await CurrentChiefAsync(organizationId, cancellationToken);
        if (chief is null) return null;
        var cycle = await db.ManagementCycles.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        var latest = (await ListHistoryAsync(organizationId, 1, cancellationToken)).FirstOrDefault();
        return new ExecutiveBriefingSettingsResponse(organizationId, chief.Id, chief.ReportsToOrganizationUserId,
            cycle?.ExecutiveBriefingEnabled ?? true, cycle?.StartupBriefingEnabled ?? true,
            cycle?.ExecutiveBriefingCadence ?? "Weekdays", cycle?.ExecutiveBriefingWeeklyDay ?? "Friday",
            cycle?.ExecutiveBriefingLocalTime ?? "09:00", cycle?.TimeZone ?? "UTC",
            cycle?.NextExecutiveBriefingAt, latest);
    }

    public async Task<ExecutiveBriefingActionResponse> UpdateSettingsAsync(Guid organizationId,
        UpdateExecutiveBriefingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var chief = await CurrentChiefAsync(organizationId, cancellationToken);
        if (chief is null) return Failure("chief_not_found", "No active Chief of Staff is assigned.");
        if (request.ManagingOrganizationUserId == chief.Id)
            return Failure("invalid_manager", "The Chief of Staff cannot report to itself.");
        var people = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == organizationId && x.IsActive).ToListAsync(cancellationToken);
        if (people.All(x => x.Id != request.ManagingOrganizationUserId))
            return Failure("invalid_manager", "The managing entity must be an active member of this organization.");
        if (CreatesCycle(chief.Id, request.ManagingOrganizationUserId, people))
            return Failure("invalid_hierarchy", "The selected manager would create a reporting cycle.");
        var cadence = request.Cadence.Trim();
        if (cadence is not ("Daily" or "Weekdays" or "Weekly"))
            return Failure("invalid_cadence", "Cadence must be Daily, Weekdays, or Weekly.");
        if (!Enum.TryParse<DayOfWeek>(request.WeeklyDay, true, out _))
            return Failure("invalid_weekly_day", "Weekly day must be a valid day of the week.");
        if (!ExecutiveBriefingScheduleCalculator.IsValidTime(request.LocalTime))
            return Failure("invalid_time", "Local time must use HH:mm format.");
        if (!ExecutiveBriefingScheduleCalculator.IsValidTimeZone(request.TimeZone))
            return Failure("invalid_time_zone", "The selected time zone is not available on this platform.");

        var now = clock.GetUtcNow();
        var cycle = await db.ManagementCycles.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (cycle is null)
        {
            cycle = new ManagementCycle { Id = Guid.NewGuid(), OrganizationId = organizationId };
            db.ManagementCycles.Add(cycle);
        }
        chief.ReportsToOrganizationUserId = request.ManagingOrganizationUserId;
        cycle.ExecutiveBriefingEnabled = request.IsEnabled;
        cycle.StartupBriefingEnabled = request.StartupEnabled;
        cycle.ExecutiveBriefingCadence = cadence;
        cycle.ExecutiveBriefingWeeklyDay = request.WeeklyDay;
        cycle.ExecutiveBriefingLocalTime = request.LocalTime;
        cycle.TimeZone = request.TimeZone;
        cycle.NextExecutiveBriefingAt = request.IsEnabled ? ExecutiveBriefingScheduleCalculator.Next(now, cycle) : null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("executive-briefing.settings-updated", nameof(ManagementCycle), cycle.Id,
            $"Executive briefing schedule updated to {cadence} at {request.LocalTime} {request.TimeZone}.", cancellationToken: cancellationToken);
        return new(true, null, "Executive briefing settings were saved.");
    }

    public Task<ExecutiveBriefingActionResponse> QueueManualAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        QueueAsync(organizationId, null, "Manual", $"briefing:manual:{Guid.NewGuid():N}", bypassStartupSetting: true, cancellationToken);

    public Task<ExecutiveBriefingActionResponse> QueueActivationAsync(Guid organizationId, Guid chiefOrganizationUserId, CancellationToken cancellationToken = default) =>
        QueueAsync(organizationId, chiefOrganizationUserId, "Activation", $"briefing:activation:{chiefOrganizationUserId:D}", false, cancellationToken);

    public async Task<ExecutiveBriefingActionResponse> QueueRuntimeStartupAsync(Guid installationId, Guid runtimeInstanceId, CancellationToken cancellationToken = default)
    {
        var chief = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.AgentInstallationId == installationId && x.IsActive &&
                        db.LeadershipAssignments.Any(a => a.OrganizationId == x.OrganizationId && a.OrganizationUserId == x.Id &&
                            a.PositionKey == "chief-of-staff" && a.EndsAt == null))
            .Select(x => new { x.OrganizationId, x.Id }).SingleOrDefaultAsync(cancellationToken);
        if (chief is null) return Failure("chief_not_found", "The registered installation is not the active Chief of Staff.");
        if (await DeferForOnboardingAsync(db, chief.OrganizationId, chief.Id, cancellationToken))
            return Failure("onboarding_in_progress", "Finish Chief onboarding before automatic briefings begin.");
        var waiting = await db.ManagementCheckInRequests.Where(x => x.OrganizationId == chief.OrganizationId &&
                x.CheckInType == "ExecutiveBriefing" && (x.Status == "Pending" || x.Status == "AwaitingReport"))
            .ToListAsync(cancellationToken);
        foreach (var pending in waiting) pending.LastDispatchedAt = null;
        if (waiting.Count > 0) await db.SaveChangesAsync(cancellationToken);
        var pendingStartup = waiting
            .Where(x => x.TriggerType is "Activation" or "RuntimeStartup")
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefault();
        if (pendingStartup is not null)
            return new(true, null, "The queued startup briefing will be dispatched by this runtime.", pendingStartup.Id);
        var key = $"briefing:runtime:{runtimeInstanceId:D}";
        var duplicate = await db.ManagementCheckInRequests.AsNoTracking()
            .Where(x => x.OrganizationId == chief.OrganizationId && x.IdempotencyKey == key)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (duplicate.HasValue) return new(true, null, "The runtime startup briefing is already queued.", duplicate);
        var cutoff = clock.GetUtcNow().AddHours(-1);
        var recent = await db.ManagementCheckInRequests.AsNoTracking()
            .Where(x => x.OrganizationId == chief.OrganizationId && x.CheckInType == "ExecutiveBriefing" &&
                        x.TriggerType == "RuntimeStartup" && x.CreatedAt >= cutoff)
            .OrderByDescending(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (recent.HasValue) return new(true, null, "A recent runtime startup briefing satisfies the startup cooldown.", recent);
        return await QueueAsync(chief.OrganizationId, chief.Id, "RuntimeStartup", key, false, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutiveBriefingHistoryItem>> ListHistoryAsync(Guid organizationId, int take = 20, CancellationToken cancellationToken = default)
    {
        var requests = await db.ManagementCheckInRequests.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.CheckInType == "ExecutiveBriefing")
            .OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(cancellationToken);
        var ids = requests.Select(x => x.Id).ToList();
        var deliveries = await db.ExecutiveBriefingDeliveries.AsNoTracking()
            .Where(x => ids.Contains(x.ManagementCheckInRequestId)).ToDictionaryAsync(x => x.ManagementCheckInRequestId, cancellationToken);
        return requests.Select(x =>
        {
            deliveries.TryGetValue(x.Id, out var delivery);
            return new ExecutiveBriefingHistoryItem(x.Id, x.TriggerType ?? "Scheduled", x.Status, x.CreatedAt, x.DueAt,
                x.DispatchAttempts, x.FailureCode, x.FailureMessage, delivery?.RecipientOrganizationUserId,
                delivery?.Channel, delivery?.Status, delivery?.ConversationId, delivery?.DeliveredAt);
        }).ToList();
    }

    private async Task<ExecutiveBriefingActionResponse> QueueAsync(Guid organizationId, Guid? expectedChiefId,
        string triggerType, string idempotencyKey, bool bypassStartupSetting, CancellationToken cancellationToken)
    {
        var chief = await CurrentChiefAsync(organizationId, cancellationToken);
        if (chief is null || expectedChiefId.HasValue && chief.Id != expectedChiefId.Value)
            return Failure("chief_not_found", "No matching active Chief of Staff is assigned.");
        if (!bypassStartupSetting && await DeferForOnboardingAsync(db, organizationId, chief.Id, cancellationToken))
            return Failure("onboarding_in_progress", "Finish Chief onboarding before automatic briefings begin.");
        var cycle = await db.ManagementCycles.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (cycle is null)
        {
            cycle = new ManagementCycle { Id = Guid.NewGuid(), OrganizationId = organizationId };
            cycle.NextExecutiveBriefingAt = ExecutiveBriefingScheduleCalculator.Next(clock.GetUtcNow(), cycle);
            db.ManagementCycles.Add(cycle);
        }
        if (!bypassStartupSetting && triggerType is "Activation" or "RuntimeStartup" && !cycle.StartupBriefingEnabled)
            return Failure("startup_disabled", "Startup executive briefings are disabled.");
        var existing = await db.ManagementCheckInRequests.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IdempotencyKey == idempotencyKey)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (existing.HasValue) return new(true, null, "The executive briefing is already queued.", existing);
        var managerId = await ResolveManagerIdAsync(chief, cancellationToken);
        if (!managerId.HasValue) return Failure("manager_not_found", "No active manager or owner is available to receive the briefing.");
        var now = clock.GetUtcNow();
        var request = new ManagementCheckInRequestRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ManagementCycleId = cycle.Id,
            RequestedByOrganizationUserId = managerId.Value, RequestedFromOrganizationUserId = chief.Id,
            CheckInType = "ExecutiveBriefing",
            TopicsJson = "[\"immediate-actions\",\"blockers\",\"decisions\",\"staffing\",\"deadlines\",\"budget\"]",
            IdempotencyKey = idempotencyKey, TriggerType = triggerType, Status = "Pending",
            CreatedAt = now, DueAt = now.AddHours(2)
        };
        db.ManagementCheckInRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("executive-briefing.queued", nameof(ManagementCheckInRequestRecord), request.Id,
            $"{triggerType} executive briefing queued for the Chief of Staff.", cancellationToken: cancellationToken);
        return new(true, null, "Executive briefing queued.", request.Id);
    }

    private async Task<OrganizationUser?> CurrentChiefAsync(Guid organizationId, CancellationToken token) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsActive &&
            db.LeadershipAssignments.Any(a => a.OrganizationId == organizationId && a.OrganizationUserId == x.Id &&
                a.PositionKey == "chief-of-staff" && a.EndsAt == null), token);

    public static async Task<bool> DeferForOnboardingAsync(CSweetDbContext db, Guid organizationId, Guid chiefId, CancellationToken token)
    {
        var onboarding = await db.AgentOnboardingEventOutbox.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.AgentOrganizationUserId == chiefId &&
            x.Status != CSweet.Domain.Communications.AgentOnboardingEventOutboxStatus.Cancelled)
            .Select(x => (CSweet.Domain.Communications.AgentOnboardingEventOutboxStatus?)x.Status).FirstOrDefaultAsync(token);
        if (onboarding is null) return false; // Preserve established installations without onboarding records.
        if (onboarding != CSweet.Domain.Communications.AgentOnboardingEventOutboxStatus.Delivered) return true;
        var installation = await db.CoreOrganizationUsers.Where(x => x.Id == chiefId).Select(x => x.AgentInstallationId).SingleAsync(token);
        return !await db.ChiefHiringPolicies.AnyAsync(x => x.OrganizationId == organizationId && x.InstallationId == installation && x.SetupComplete, token);
    }

    private async Task<Guid?> ResolveManagerIdAsync(OrganizationUser chief, CancellationToken token)
    {
        if (chief.ReportsToOrganizationUserId is { } managerId && await db.CoreOrganizationUsers.AnyAsync(
                x => x.Id == managerId && x.OrganizationId == chief.OrganizationId && x.IsActive, token)) return managerId;
        return await db.CoreOrganizationUsers.Where(x => x.OrganizationId == chief.OrganizationId && x.IsActive &&
                x.PermissionLevel == OrganizationPermissionLevel.Owner && x.Id != chief.Id)
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
    }

    private static bool CreatesCycle(Guid chiefId, Guid managerId, IReadOnlyCollection<OrganizationUser> people)
    {
        var byId = people.ToDictionary(x => x.Id);
        var seen = new HashSet<Guid> { chiefId };
        var current = managerId;
        while (byId.TryGetValue(current, out var person))
        {
            if (!seen.Add(current)) return true;
            if (!person.ReportsToOrganizationUserId.HasValue) return false;
            current = person.ReportsToOrganizationUserId.Value;
        }
        return false;
    }

    private static ExecutiveBriefingActionResponse Failure(string code, string message) => new(false, code, message);
}
