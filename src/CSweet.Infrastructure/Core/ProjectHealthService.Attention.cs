using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using Microsoft.EntityFrameworkCore;
using CSweet.Infrastructure.Communications;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    internal async Task<IReadOnlyList<ProjectIncidentAttentionResponse>> ReadAttentionAsync(Guid organization, Guid actor, CancellationToken token)
    {
        // A delivered report is evidence of routing, never a permanent access grant.
        var incidents = await db.ProjectIncidents.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.CurrentRecipientId == actor && x.Status == "Open").OrderBy(x => x.DetectedAt).Take(100).ToListAsync(token);
        var result = new List<ProjectIncidentAttentionResponse>();
        var observations = new Dictionary<Guid, HealthObservation>();
        var canRetry = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor &&
            x.OrganizationId == organization && x.IsActive && x.EmployeeType == EmployeeType.Human &&
            x.PermissionLevel >= OrganizationPermissionLevel.Manager, token);
        foreach (var incident in incidents)
        {
            if (!await CanReadAsync(organization, actor, incident.WorkstreamId, incident, token)) continue;
            if (!await IncidentRequiresOwnerInputAsync(incident, token, observations)) continue;
            var delivery = await db.ProjectIncidentDeliveries.AsNoTracking().Where(x => x.IncidentId == incident.Id &&
                x.RecipientId == actor && x.DeliveredAt != null && x.ConversationId != null)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(token);
            if (delivery is null) continue;
            var decision = await ReadIncidentDecisionCardAsync(incident, delivery.ConversationId!.Value, actor, token);
            // Preparing choices and handling an already answered choice are manager work.
            if (incident.OwnerReportJson is not null && decision?.Status != "Pending") continue;
            var project = await db.Workstreams.AsNoTracking().SingleAsync(x => x.OrganizationId == organization && x.Id == incident.WorkstreamId, token);
            var reporter = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && x.Id == (incident.ReporterOrganizationUserId ?? delivery.SenderId))
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
                presentation.Description, presentation.NextStep, delivery.ConversationId is { } reportChat
                    ? $"/organizations/{organization}/communications/{reportChat}" : presentation.Link, incident.DetectedAt)
                { Collaboration = collaboration, ReporterEmployeeId = delivery.SenderId,
                    ReporterOrganizationUserId = incident.ReporterOrganizationUserId ?? delivery.SenderId,
                    OwnerReport = presentation.Description, ReportConversationId = delivery.ConversationId,
                    ReportDeliveryPending = delivery.DeliveredAt is null,
                    ResolutionDecision = decision,
                    InvestigateUri = presentation.Link, InvestigateLabel = presentation.LinkLabel });
        }
        return result;
    }

    private async Task<CSweet.Contracts.Communications.ExecutiveDecisionCardResponse?> ReadIncidentDecisionCardAsync(ProjectIncident incident, Guid chat,
        Guid actor, CancellationToken token)
    {
        var decisions = await db.ExecutiveDecisions.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
            x.ConversationId == chat).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
        var decision = decisions.FirstOrDefault(x => ExecutiveDecisionServiceIncidentBinding(x) is { } binding &&
            binding.IncidentId == incident.Id && binding.ReportRevision == incident.ReportRevision);
        if (decision is null) return null;
        var card = ExecutiveDecisionService.IncidentCard(decision);
        if (decision.Status == ExecutiveDecisionStatus.Pending && OwnerReport(incident)?.SourceHash != (await CurrentIncidentStateAsync(incident, token)).Hash)
            card = card with { Status = "Outdated" };
        return card;
    }
}
