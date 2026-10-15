using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    // Routing an incident to a person does not prove that person still has work to do.
    // Keep the episode open for recovery tracking while projecting only current blockers.
    internal async Task<bool> IncidentRequiresOwnerInputAsync(ProjectIncident incident, CancellationToken token,
        IDictionary<Guid, HealthObservation>? observations = null)
    {
        if (incident.Status != "Open" ||
            incident.Disposition is IncidentDispositions.Investigating or IncidentDispositions.AwaitingRecovery &&
            incident.ReviewAt > clock.GetUtcNow()) return false;
        if (OwnerReport(incident) is { } report &&
            report.SourceHash != (await CurrentIncidentStateAsync(incident, token)).Hash) return false;
        return await HasCurrentIncidentFailureAsync(incident, token, observations);
    }

    private async Task<bool> HasCurrentIncidentFailureAsync(ProjectIncident incident, CancellationToken token,
        IDictionary<Guid, HealthObservation>? observations = null)
    {
        // A broken monitor cannot supply an observation; its own incident remains actionable.
        if (incident.Fingerprint == "health-assessment")
            return await db.Workstreams.AsNoTracking().AnyAsync(x => x.Id == incident.WorkstreamId &&
                x.OrganizationId == incident.OrganizationId && x.Status != WorkstreamStatus.Completed && x.Status != WorkstreamStatus.Cancelled, token);
        if (observations is null || !observations.TryGetValue(incident.WorkstreamId, out var observation))
        {
            var project = await db.Workstreams.AsNoTracking().SingleAsync(x =>
                x.Id == incident.WorkstreamId && x.OrganizationId == incident.OrganizationId, token);
            if (project.Status is WorkstreamStatus.Completed or WorkstreamStatus.Cancelled) return false;
            observation = await reader.ObserveAsync(project, token);
            if (observations is not null) observations[project.Id] = observation;
        }
        return incident.Fingerprint == "idle"
            ? !observation.Active && observation.Waiting is null && observation.LastProgress <= incident.LastProgressAt &&
                clock.GetUtcNow() - observation.LastProgress >= Threshold
            : observation.Failures.Any(x => x.Key == incident.Fingerprint);
    }

    private async Task WithdrawIncidentAttentionAsync(ProjectIncident incident, CancellationToken token)
    {
        await CloseIncidentQuestionsAsync(incident, token);
        var changed = db.ChangeTracker.Entries<ExecutiveDecision>().Any(x => x.State == EntityState.Modified &&
            x.Entity.Status == ExecutiveDecisionStatus.Cancelled);
        var keys = await db.ProjectIncidentDeliveries.Where(x => x.IncidentId == incident.Id)
            .Select(x => x.Id).ToArrayAsync(token);
        var notificationKeys = keys.Select(x => $"project-health:{x:N}").ToArray();
        foreach (var notification in await db.UserNotifications.Where(x => x.OrganizationId == incident.OrganizationId &&
                     x.Category == "ProjectHealth" && x.DismissedAt == null && notificationKeys.Contains(x.DeduplicationKey!)).ToListAsync(token))
        {
            notification.DismissedAt = clock.GetUtcNow();
            changed = true;
        }
        if (changed) QueueAttentionChange(incident, incident.CurrentRecipientId);
    }
}
