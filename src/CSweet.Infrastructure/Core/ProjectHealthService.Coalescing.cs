using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private async Task CoalesceCoordinationIncidentsAsync(Workstream project, List<ProjectIncident> open, CancellationToken token)
    {
        foreach (var incident in open.Where(x => x.Status == "Open" && x.Fingerprint.StartsWith("work:", StringComparison.Ordinal)).ToArray())
        {
            if (IncidentSource(incident.Fingerprint, "work") is not { } workId) continue;
            var work = await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId &&
                x.OrganizationId == project.OrganizationId.ToString() && x.SourceType == "agent-coordination", token);
            if (!Guid.TryParse(work?.CorrelationId, out var sessionId) ||
                !await db.AgentCoordinationSessions.AnyAsync(x => x.Id == sessionId && x.OrganizationId == project.OrganizationId && x.WorkstreamId == project.Id, token)) continue;
            var fingerprint = $"coordination:{sessionId:N}";
            var canonical = open.SingleOrDefault(x => x.Status == "Open" && x.Fingerprint == fingerprint);
            if (canonical is null)
            {
                incident.Fingerprint = fingerprint; incident.Reason = "Planning coordination stopped"; incident.Revision++;
                await NotifyAsync(incident, token);
                continue;
            }
            canonical.EvidenceJson = JsonSerializer.Serialize(Deserialize<ProjectDiagnosticEvidence>(canonical.EvidenceJson)
                .Concat(Deserialize<ProjectDiagnosticEvidence>(incident.EvidenceJson))
                .DistinctBy(x => new { x.Kind, x.SourceId, x.Detail }).TakeLast(100).ToArray(), Json);
            if (incident.DetectedAt < canonical.DetectedAt) canonical.DetectedAt = incident.DetectedAt;
            if (canonical.EscalateAt.HasValue && incident.EscalateAt < canonical.EscalateAt) canonical.EscalateAt = incident.EscalateAt;
            canonical.Revision++;
            // Consolidation is not recovery. Retain both audit trails and the surviving deadline.
            incident.Status = "Superseded"; incident.EscalateAt = null; incident.ReviewAt = null; incident.Revision++;
            await CloseIncidentQuestionsAsync(incident, token);
            var pending = await db.ProjectIncidentDeliveries.Where(x => x.IncidentId == incident.Id && x.DeliveredAt == null).ToListAsync(token);
            db.ProjectIncidentDeliveries.RemoveRange(pending);
            var recipients = await db.ProjectIncidentDeliveries.Where(x => x.IncidentId == incident.Id && x.DeliveredAt != null)
                .Select(x => x.RecipientId).Distinct().ToListAsync(token);
            foreach (var recipient in recipients.Append(incident.CurrentRecipientId).Distinct()) QueueAttentionChange(incident, recipient);
            db.QueueAudit(new("project-health.incident.superseded", OrganizationId: project.OrganizationId,
                EntityType: nameof(ProjectIncident), EntityId: incident.Id, Summary: $"Duplicate failure is tracked by incident {canonical.Id}.", OccurredAt: clock.GetUtcNow()));
            await NotifyAsync(canonical, token);
        }
    }
}
