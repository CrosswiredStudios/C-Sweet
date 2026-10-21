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
            x.CurrentRecipientId == actor && x.Status == "Open").OrderBy(x => x.DetectedAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        var result = new List<ProjectIncidentAttentionResponse>();
        var planningIssues = new Dictionary<(Guid Project, Guid Session), int>();
        var observations = new Dictionary<Guid, HealthObservation>();
        var canRetry = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor &&
            x.OrganizationId == organization && x.IsActive && x.EmployeeType == EmployeeType.Human &&
            x.PermissionLevel >= OrganizationPermissionLevel.Manager, token);
        foreach (var incident in incidents)
        {
            if (!await CanReadAsync(organization, actor, incident.WorkstreamId, incident, token)) continue;
            var failure = await TechnicalPlanningFailureAsync(incident, token);
            var requiresInput = await IncidentRequiresOwnerInputAsync(incident, token, observations);
            var statusReport = !requiresInput && incident.Disposition == CSweet.Agent.SDK.IncidentDispositions.Escalate &&
                (failure is not null || OwnerReport(incident) is null or { RequiresOwnerInput: false }) &&
                await HasCurrentIncidentFailureAsync(incident, token, observations);
            if (!requiresInput && !statusReport) continue;
            var delivery = await db.ProjectIncidentDeliveries.AsNoTracking().Where(x => x.IncidentId == incident.Id &&
                x.RecipientId == actor && x.DeliveredAt != null && x.ConversationId != null)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(token);
            if (delivery is null) continue;
            var decision = requiresInput ? await ReadIncidentDecisionCardAsync(incident, delivery.ConversationId!.Value, actor, token) : null;
            // Preparing choices and handling an already answered choice are manager work.
            if (requiresInput && incident.OwnerReportJson is not null && decision?.Status != "Pending") continue;
            var project = await db.Workstreams.AsNoTracking().SingleAsync(x => x.OrganizationId == organization && x.Id == incident.WorkstreamId, token);
            var reporter = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && x.Id == (incident.ReporterOrganizationUserId ?? delivery.SenderId))
                .Select(x => x.DisplayName).SingleOrDefaultAsync(token);
            var presentation = await PresentAsync(incident, token);
            var technical = failure?.Diagnostic;
            // Only collapse reports bound to the exact same planning cycle, after authorizing
            // each record. Similar prose or a shared project never establishes root identity.
            if (failure?.SessionId is { } root && planningIssues.TryGetValue((project.Id, root), out var existingIndex))
            {
                result[existingIndex] = result[existingIndex] with { RelatedIncidentCount = result[existingIndex].RelatedIncidentCount + 1 };
                continue;
            }
            ProjectIncidentCollaborationResponse? collaboration = null;
            if ((failure?.SessionId ?? IncidentSource(incident.Fingerprint, "coordination")) is { } sessionId)
            {
                var session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == sessionId && x.OrganizationId == organization && x.WorkstreamId == project.Id, token);
                if (session is not null)
                    collaboration = new(session.Id, session.ConversationId, session.Revision, session.Status.ToString(),
                        canRetry && technical is null && session.Status is (AgentCoordinationStatus.Failed or AgentCoordinationStatus.Blocked),
                        $"/organizations/{organization}/communications/{session.ConversationId}?perspective={session.InitiatorOrganizationUserId}&collaboration={session.Id}");
            }
            result.Add(new(incident.Id, project.Id, project.Name, reporter ?? "Project manager", presentation.Title,
                presentation.Summary, presentation.NextStep, delivery.ConversationId is { } reportChat
                    ? $"/organizations/{organization}/communications/{reportChat}" : presentation.Link, incident.DetectedAt)
                { Collaboration = collaboration, ReporterEmployeeId = delivery.SenderId,
                    ReporterOrganizationUserId = incident.ReporterOrganizationUserId ?? delivery.SenderId,
                    IsStatusReport = statusReport, RecoveryStatus = presentation.Recovery, DiagnosticDetails = technical,
                    OwnerReport = presentation.Summary, ReportConversationId = delivery.ConversationId,
                    ReportDeliveryPending = delivery.DeliveredAt is null,
                    ResolutionDecision = decision,
                    InvestigateUri = technical is not null && collaboration is not null ? collaboration.InvestigateUri : presentation.Link,
                    InvestigateLabel = technical is not null && collaboration is not null ? "Review collaboration" : presentation.LinkLabel });
            if (failure?.SessionId is { } source) planningIssues[(project.Id, source)] = result.Count - 1;
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
