using CSweet.Contracts.Core;
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
            result.Add(new(incident.Id, project.Id, project.Name, reporter ?? "Project manager", incident.Reason,
                incident.Facts, incident.RecommendedAction, $"/organizations/{organization}/communications/{delivery.ConversationId}", incident.DetectedAt));
        }
        return result;
    }
}
