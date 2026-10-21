using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private void CaptureAgentPresenceEvents()
    {
        // Capture only readiness changes, never heartbeat, log or stream updates.
        // The outbox commits with the state; clients reread authorized current state.
        var installationIds = ChangeTracker.Entries<AgentRuntimeInstance>()
            .Where(x => PresenceChanged(x, nameof(AgentRuntimeInstance.Status), nameof(AgentRuntimeInstance.Reason)))
            .Select(x => x.Entity.AgentInstallationId)
            .Concat(ChangeTracker.Entries<AgentInstallation>()
                .Where(x => PresenceChanged(x, nameof(AgentInstallation.IsEnabled),
                    nameof(AgentInstallation.RevisionStatus), nameof(AgentInstallation.SetupState),
                    nameof(AgentInstallation.ConfigurationSyncStatus), nameof(AgentInstallation.ConfigurationSyncLastError)))
                .Select(x => x.Entity.Id))
            .Concat(ChangeTracker.Entries<AgentSchedule>()
                .Where(x => PresenceChanged(x, nameof(AgentSchedule.ActivationMode), nameof(AgentSchedule.IsEnabled),
                    nameof(AgentSchedule.NextTickAt), nameof(AgentSchedule.AutomaticStartSuppressedAt),
                    nameof(AgentSchedule.ConsecutiveStartupFailures)))
                .Select(x => x.Entity.AgentInstallationId))
            .ToHashSet();
        if (installationIds.Count == 0) return;

        var organizations = CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.IsActive && x.EmployeeType == EmployeeType.Agent &&
                x.AgentInstallationId.HasValue && installationIds.Contains(x.AgentInstallationId.Value))
            .Select(x => x.OrganizationId).ToHashSet();
        organizations.UnionWith(ChangeTracker.Entries<OrganizationUser>()
            .Where(x => x.State != EntityState.Deleted && x.Entity.IsActive &&
                x.Entity.EmployeeType == EmployeeType.Agent && x.Entity.AgentInstallationId.HasValue &&
                installationIds.Contains(x.Entity.AgentInstallationId.Value))
            .Select(x => x.Entity.OrganizationId));

        foreach (var organizationId in organizations)
            QueueApplicationRealtimeEvent(organizationId, null, null, AppRealtimeEvents.AgentPresenceChanged,
                $"organizations/{organizationId:D}/agent-presence", "{}", DateTimeOffset.UtcNow,
                ResolveActiveOrganizationRecipients(organizationId));
    }

    private static bool PresenceChanged<T>(EntityEntry<T> entry, params string[] properties) where T : class =>
        entry.State is EntityState.Added or EntityState.Deleted ||
        entry.State == EntityState.Modified && properties.Any(name =>
            !Equals(entry.OriginalValues[name], entry.CurrentValues[name]));
}
