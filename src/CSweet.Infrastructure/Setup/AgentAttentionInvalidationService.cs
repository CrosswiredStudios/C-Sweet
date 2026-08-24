using CSweet.Application.Setup;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentAttentionInvalidationService(
    CSweetDbContext db,
    TimeProvider clock) : IAgentAttentionInvalidationService
{
    public async Task InvalidateManagersAsync(
        Guid organizationId,
        string triggerCategory,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var installationIds = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(manager => manager.OrganizationId == organizationId && manager.IsActive &&
                manager.AgentInstallationId.HasValue &&
                (db.CoreOrganizationUsers.Any(report =>
                     report.OrganizationId == organizationId && report.IsActive &&
                     report.ReportsToOrganizationUserId == manager.Id) ||
                 db.OrganizationTeams.Any(team =>
                     team.OrganizationId == organizationId && team.ArchivedAt == null &&
                     team.LeadOrganizationUserId == manager.Id)))
            .Select(manager => manager.AgentInstallationId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        await InvalidateAsync(installationIds, triggerCategory, correlationId, cancellationToken);
    }

    public async Task InvalidateAsync(
        IReadOnlyCollection<Guid> installationIds,
        string triggerCategory,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (installationIds.Count == 0) return;
        if (string.IsNullOrWhiteSpace(triggerCategory) || triggerCategory.Length > 80)
            throw new ArgumentException("Attention trigger category is required and bounded.", nameof(triggerCategory));
        var ids = installationIds.Where(x => x != Guid.Empty).Distinct().ToList();
        var schedules = await db.AgentSchedules.Where(x => ids.Contains(x.AgentInstallationId) &&
            x.IsEnabled && x.AgentInstallation!.IsEnabled).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var schedule in schedules)
        {
            schedule.PendingAttentionReason = AgentAttentionReasons.StateChanged;
            schedule.PendingAttentionTriggerCategory = triggerCategory;
            schedule.PendingAttentionCorrelationId = correlationId;
            schedule.AttentionInvalidatedAt ??= now;
            if (!schedule.NextAttentionReviewAt.HasValue || schedule.NextAttentionReviewAt > now)
                schedule.NextAttentionReviewAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
