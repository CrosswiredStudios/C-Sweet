using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    internal async Task<IReadOnlyList<ProjectIncidentAttentionResponse>> ReadAttentionAsync(Guid organization, Guid actor, CancellationToken token)
    {
        // A delivered report is evidence of routing, never a permanent access grant.
        var incidents = await db.ProjectIncidents.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.CurrentRecipientId == actor && x.Status == "Open").OrderBy(x => x.DetectedAt).Take(100).ToListAsync(token);
        var result = new List<ProjectIncidentAttentionResponse>();
        var canRetry = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor &&
            x.OrganizationId == organization && x.IsActive && x.EmployeeType == EmployeeType.Human &&
            x.PermissionLevel >= OrganizationPermissionLevel.Manager, token);
        foreach (var incident in incidents)
        {
            if (!await CanReadAsync(organization, actor, incident.WorkstreamId, incident, token)) continue;
            var delivery = await db.ProjectIncidentDeliveries.AsNoTracking().Where(x => x.IncidentId == incident.Id &&
                x.RecipientId == actor && x.DeliveredAt != null && x.ConversationId != null)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(token);
            if (delivery is null) continue;
            var project = await db.Workstreams.AsNoTracking().SingleAsync(x => x.OrganizationId == organization && x.Id == incident.WorkstreamId, token);
            var reporter = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && x.Id == delivery.SenderId)
                .Select(x => x.DisplayName).SingleOrDefaultAsync(token);
            var presentation = await PresentAsync(incident, token);
            ProjectIncidentCollaborationResponse? collaboration = null;
            if (IncidentSource(incident.Fingerprint, "coordination") is { } sessionId)
            {
                var session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == sessionId && x.OrganizationId == organization && x.WorkstreamId == project.Id, token);
                if (session is not null)
                    collaboration = new(session.Id, session.ConversationId, session.Revision, session.Status.ToString(),
                        canRetry && session.Status is (AgentCoordinationStatus.Failed or AgentCoordinationStatus.Blocked),
                        $"/organizations/{organization}/communications/{session.ConversationId}?perspective={session.InitiatorOrganizationUserId}&collaboration={session.Id}");
            }
            result.Add(new(incident.Id, project.Id, project.Name, reporter ?? "Project manager", presentation.Title,
                presentation.Summary, presentation.NextStep, $"/organizations/{organization}/communications/{delivery.ConversationId}", incident.DetectedAt)
                { Collaboration = collaboration });
        }
        return result;
    }
}
