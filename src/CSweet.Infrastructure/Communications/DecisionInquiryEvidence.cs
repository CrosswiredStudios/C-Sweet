using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Contracts.Core;
using System.Text.Json;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

internal static class DecisionInquiryEvidence
{
    internal static async Task<DecisionInquiryContext?> ReadAsync(CSweetDbContext db, Guid org,
        ConversationMessage message, Guid employee, CancellationToken token)
    {
        if (message.SourceProvider != DecisionFollowUpService.Provider) return null;
        var parts = message.SourceChannelExternalId?.Split('/', 2);
        if (parts?.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id)) throw new UnauthorizedAccessException();
        var source = await db.ExecutiveDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.OrganizationId == org &&
            d.ConversationId == message.ConversationId && d.Id == id, token) ?? throw new UnauthorizedAccessException();
        var audience = await db.ConversationParticipants.AsNoTracking().Where(p => p.ConversationId == message.ConversationId &&
            p.LeftAt == null && p.OrganizationUser!.IsActive && p.OrganizationUser.OrganizationId == org &&
            p.Conversation!.ArchivedAt == null).Select(p => p.OrganizationUser!).ToArrayAsync(token);
        var speaker = audience.SingleOrDefault(p => p.Id == employee && p.AgentInstallationId == source.RequestingInstallationId);
        if (speaker is null || !audience.Any(p => p.Id == message.SenderOrganizationUserId && p.EmployeeType == EmployeeType.Human))
            throw new UnauthorizedAccessException();
        var card = ExecutiveDecisionService.IncidentCard(source);
        var choices = card.Options.Where(o => parts[1] == "" || o.Id == parts[1]);
        var option = string.Join("\n", choices.Select(o => $"{o.Label}. {o.Description} {o.Impact}"));
        var facts = new List<ProjectDiagnosticEvidence>();
        var documents = new List<IncidentDocumentReference>();
        var owners = new Dictionary<Guid, string>();
        Guid? incidentId = card.IncidentId;
        if (ExecutiveDecisionService.LinkedProjectDecision(source) is Guid linkedId)
        {
            var decision = await db.WorkstreamDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.OrganizationId == org && d.Id == linkedId, token);
            if (decision is not null && await CanShareDecisionAsync(db, org, decision.WorkstreamId, audience, token))
            {
                facts.Add(new("Project decision", decision.Id, decision.UpdatedAt,
                    $"{decision.Summary}\n{decision.BlockingImpact}\n{decision.Status}. {decision.Rationale}",
                    $"/organizations/{org}/projects/{decision.WorkstreamId}"));
                owners[decision.RequestedByOrganizationUserId] = "Requested the project decision";
                var refs = JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.EvidenceReference[]>(decision.EvidenceJson, JsonSerializerOptions.Web) ?? [];
                if (incidentId is null)
                {
                    var incidentRefs = refs.Where(r => string.Equals(r.Kind, "incident", StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.ResourceId).Distinct().Take(16).ToArray();
                    var matches = await db.ProjectIncidents.AsNoTracking().Where(i => i.OrganizationId == org &&
                        i.WorkstreamId == decision.WorkstreamId && incidentRefs.Contains(i.Id)).Select(i => i.Id).ToArrayAsync(token);
                    incidentId = incidentRefs.Where(matches.Contains).Select(id => (Guid?)id).FirstOrDefault();
                }
                documents.AddRange(refs.Where(r => r.RevisionId.HasValue && r.Digest is { Length: 64 })
                    .Select(r => new IncidentDocumentReference(r.ResourceId, r.RevisionId!.Value, r.Digest!)));
            }
        }
        Guid? authorizedIncident = null;
        if (incidentId.HasValue)
        {
            var health = new ProjectHealthService(db, new(db, TimeProvider.System), TimeProvider.System);
            var incident = await db.ProjectIncidents.AsNoTracking().SingleOrDefaultAsync(i => i.OrganizationId == org && i.Id == incidentId, token);
            if (incident is not null && await health.CanShareDiagnosticsAsync(incident, speaker, audience, token))
            {
                authorizedIncident = incident.Id;
                var state = await health.CurrentIncidentStateAsync(incident, token);
                documents.AddRange(state.Documents);
                facts.AddRange(state.Facts.Where(f => f.Kind != "CurrentRecoveryExecution"));
                facts.Add(new("Incident", incident.Id, incident.DetectedAt, incident.Facts + "\n" + incident.MissingEvidence));
                if (incident.AffectedWorkItemId is Guid item)
                {
                    var assignee = await db.CoreWorkTasks.AsNoTracking().Where(t => t.OrganizationId == org && t.Id == item)
                        .Select(t => t.AssignedEmployeeId).SingleOrDefaultAsync(token);
                    if (assignee is Guid owner) owners[owner] = "Assigned to the affected work";
                }
                owners.TryAdd(incident.ProducerEmployeeId, "Responsible for the project incident");
                var sessions = state.Facts.Where(f => f.Kind == "StoppedCollaboration").Select(f => f.SourceId).ToArray();
                foreach (var session in await db.AgentCoordinationSessions.AsNoTracking().Where(s => s.OrganizationId == org &&
                    s.WorkstreamId == incident.WorkstreamId && sessions.Contains(s.Id))
                    .Select(s => new { s.InitiatorOrganizationUserId, s.TargetOrganizationUserId, s.Subject }).ToArrayAsync(token))
                {
                    owners.TryAdd(session.TargetOrganizationUserId, "Contributed to " + session.Subject);
                    owners.TryAdd(session.InitiatorOrganizationUserId, "Requested " + session.Subject);
                }
            }
        }
        // Standalone decisions may have been raised against a document in the source chat turn.
        var linked = await (from a in db.ConversationMessageArtifacts.AsNoTracking()
            join m in db.CoreConversationMessages on a.MessageId equals m.Id
            join file in db.CoreArtifacts on a.ArtifactId equals file.Id
            join r in db.ArtifactRevisions on (a.RevisionId ?? file.AcceptedRevisionId ?? file.LatestRevisionId) equals r.Id
            where a.OrganizationId == org && a.ConversationId == source.ConversationId && m.ConversationId == source.ConversationId &&
                (m.Id == source.ConversationMessageId || source.ChatTurnId.HasValue && m.ChatTurnId == source.ChatTurnId) &&
                r.ArtifactId == file.Id && r.OrganizationId == org && file.OrganizationId == org && file.ArchivedAt == null
            orderby a.CreatedAt, a.Id
            select new { file.Id, Revision = r.Id, r.ContentSha256, file.StewardOrganizationUserId }).Take(8).ToArrayAsync(token);
        documents.AddRange(linked.Select(d => new IncidentDocumentReference(d.Id, d.Revision, d.ContentSha256)));
        var allowed = new List<IncidentDocumentReference>();
        foreach (var reference in documents.Distinct().Take(8))
        {
            if (!await CanReadDocumentAsync(db, org, reference.ArtifactId, audience, token)) continue;
            allowed.Add(reference);
            if (linked.FirstOrDefault(d => d.Id == reference.ArtifactId)?.StewardOrganizationUserId is Guid steward)
                owners.TryAdd(steward, "Responsible for the linked document");
        }
        var ownerIds = owners.Keys.ToArray();
        var teams = db.TeamMemberships.Where(m => m.OrganizationId == org && m.OrganizationUserId == employee && m.EndedAt == null).Select(m => m.TeamId);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(p => p.OrganizationId == org && p.Id != employee &&
            p.IsActive && p.ArchivedAt == null && p.EmployeeType == EmployeeType.Agent &&
            p.AgentInstallation!.IsEnabled && p.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active &&
            p.AgentInstallation.Grant != null && (ownerIds.Contains(p.Id) ||
                p.ReportsToOrganizationUserId == employee || p.Id == speaker.ReportsToOrganizationUserId ||
                db.TeamMemberships.Any(m => m.OrganizationId == org && m.OrganizationUserId == p.Id && m.EndedAt == null && teams.Contains(m.TeamId))))
            .OrderByDescending(p => ownerIds.Contains(p.Id)).ThenBy(p => p.DisplayName).ThenBy(p => p.Id).Take(40)
            .Select(p => new { p.Id, p.DisplayName, Role = p.Role!.Name, p.AgentInstallation!.Grant!.RequiredCapabilitiesJson }).ToArrayAsync(token);
        var reachable = people.Where(p =>
        {
            var grants = JsonSerializer.Deserialize<string[]>(p.RequiredCapabilitiesJson) ?? [];
            return grants.Contains(CommunicationCapabilities.CoordinationRead) && grants.Contains(CommunicationCapabilities.CoordinationRespond);
        }).ToArray();
        var contacts = reachable.Select((p, index) => new DecisionInquiryContact(p.Id,
            reachable.Count(other => string.Equals(other.DisplayName, p.DisplayName, StringComparison.OrdinalIgnoreCase)) == 1
                ? p.DisplayName : $"{p.DisplayName} ({p.Role ?? "colleague"}, {index + 1})",
            owners.GetValueOrDefault(p.Id, p.Role ?? "Colleague"))).ToArray();
        static string Preview(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
        return new(id, Preview(card.Prompt, 4500), Preview(option, 3000),
            facts.OrderByDescending(f => f.Kind == "StoppedCollaboration").ThenByDescending(f => f.Kind == "CurrentWorkItem")
                .ThenBy(f => f.SourceId).Take(12).Select(f => f with { Detail = Preview(f.Detail, 1200) }).ToArray(), contacts, authorizedIncident)
            { Documents = allowed };
    }

    private static async Task<bool> CanShareDecisionAsync(CSweetDbContext db, Guid org, Guid project, OrganizationUser[] audience, CancellationToken token)
    {
        var teams = db.WorkstreamTeamAssignments.Where(t => t.OrganizationId == org && t.WorkstreamId == project && t.EndsAt == null)
            .Select(t => t.TeamId);
        foreach (var person in audience)
        {
            if (person.EmployeeType == EmployeeType.Human && person.PermissionLevel == OrganizationPermissionLevel.Owner) continue;
            if (person.EmployeeType == EmployeeType.Agent && !await HasGrantAsync(db, person, PlatformCapabilities.DecisionRead, token)) return false;
            if (!await db.Workstreams.BusinessProjects().AnyAsync(p => p.Id == project && p.OrganizationId == org &&
                (p.AccountableManagerOrganizationUserId == person.Id || db.WorkstreamSupervisionAssignments.Any(s => s.OrganizationId == org &&
                    s.WorkstreamId == project && s.SupervisorOrganizationUserId == person.Id && s.EndsAt == null) ||
                db.TeamMemberships.Any(m => m.OrganizationId == org && m.OrganizationUserId == person.Id && m.EndedAt == null && teams.Contains(m.TeamId))), token))
                return false;
        }
        return true;
    }

    private static async Task<bool> HasGrantAsync(CSweetDbContext db, OrganizationUser person, string capability, CancellationToken token)
    {
        var grant = await db.AgentInstallations.AsNoTracking().Where(i => i.Id == person.AgentInstallationId &&
                i.IsEnabled && i.RevisionStatus == PluginRevisionStatus.Active && i.Grant != null)
            .Select(i => i.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(token);
        return (JsonSerializer.Deserialize<string[]>(grant ?? "[]") ?? []).Contains(capability);
    }

    internal static async Task<bool> CanReadDocumentAsync(CSweetDbContext db, Guid org, Guid file,
        OrganizationUser[] audience, CancellationToken token)
    {
        foreach (var person in audience)
        {
            if (person.EmployeeType == EmployeeType.Human && person.PermissionLevel >= OrganizationPermissionLevel.Manager) continue;
            var agent = person.EmployeeType == EmployeeType.Agent;
            if (agent && !await HasGrantAsync(db, person, PlatformCapabilities.ArtifactRead, token)) return false;
            var kind = agent ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
            var subject = agent ? person.AgentInstallationId : person.Id;
            if (!await db.ScopedActionGrants.AsNoTracking().AnyAsync(g => g.OrganizationId == org && g.SubjectKind == kind &&
                    g.SubjectId == subject && g.ScopeKind == GrantScopeKind.Artifact && g.ScopeId == file && g.Action == ArtifactActions.Read &&
                    g.RevokedAt == null && (g.ExpiresAt == null || g.ExpiresAt > DateTimeOffset.UtcNow), token)) return false;
        }
        return true;
    }
}
