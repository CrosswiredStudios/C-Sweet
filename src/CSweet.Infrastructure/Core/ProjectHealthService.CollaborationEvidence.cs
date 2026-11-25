using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private async Task<Guid?> ResolveCorrectionCommitmentAsync(ProjectIncident incident, CancellationToken token)
    {
        if (IncidentSource(incident.Fingerprint, "coordination") is not Guid sessionId) return null;
        var session = await db.AgentCoordinationSessions.AsNoTracking().Where(s => s.Id == sessionId &&
            s.OrganizationId == incident.OrganizationId && s.WorkstreamId == incident.WorkstreamId &&
            (s.Status == AgentCoordinationStatus.Blocked || s.Status == AgentCoordinationStatus.Failed))
            .Select(s => new { s.SourceKind, s.SourceWorkItemId, s.SourceBoardId, s.InitiatorOrganizationUserId,
                s.InitiatorInstallationId, Fingerprint = s.Turns.Where(t => t.Ordinal == 0 &&
                    t.SpeakerOrganizationUserId == s.InitiatorOrganizationUserId).Select(t => t.ArtifactKey).FirstOrDefault() })
            .SingleOrDefaultAsync(token);
        if (session is null) return null;
        if (session.SourceWorkItemId is Guid source) return source;
        if (session.SourceKind != "Board" || session.SourceBoardId is not Guid board) return null;

        var commitments = await db.CoreWorkTasks.AsNoTracking().Where(t => t.OrganizationId == incident.OrganizationId &&
            t.AssignedEmployeeId == session.InitiatorOrganizationUserId && t.AssignedAgentInstallationId == session.InitiatorInstallationId &&
            t.ArchivedAt == null && t.Status == WorkTaskStatus.Blocked && t.Board != null && t.Board.ArchivedAt == null &&
            t.Board.Kind == WorkBoardKind.Personal && t.PersonalWorkContextJson != null)
            .OrderBy(t => t.Id).Take(101).Select(t => new { t.Id, t.PersonalWorkContextJson }).ToListAsync(token);
        if (commitments.Count > 100) return null;
        var matches = commitments.Where(t => Matches(t.PersonalWorkContextJson!)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;

        bool Matches(string json)
        {
            try
            {
                var context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(json, Json);
                return context?.WorkstreamId == incident.WorkstreamId && context.BoardId == board &&
                    (context.CoordinationSessionId == sessionId || !string.IsNullOrWhiteSpace(session.Fingerprint) &&
                        context.SourceFingerprint == session.Fingerprint);
            }
            catch (JsonException) { return false; }
        }
    }

    private async Task<ProjectDiagnosticEvidence[]> StoppedCollaborationEvidenceAsync(ProjectIncident incident,
        WorkTask? ticket, CancellationToken token)
    {
        var sessions = db.AgentCoordinationSessions.AsNoTracking().Where(s => s.OrganizationId == incident.OrganizationId &&
            s.WorkstreamId == incident.WorkstreamId && (s.Status == AgentCoordinationStatus.Blocked ||
                s.Status == AgentCoordinationStatus.Failed || s.Status == AgentCoordinationStatus.Cancelled));
        if (IncidentSource(incident.Fingerprint, "coordination") is Guid direct)
            sessions = sessions.Where(s => s.Id == direct);
        else if (ticket is not null && db.HealthProjectForTask(ticket) == incident.WorkstreamId)
        {
            // A manager's recovery commitment records the producer's original ticket as its work item.
            var workItem = CSweetDbContext.ReadGuidProperty(ticket.PersonalWorkContextJson, "workItemId");
            if (workItem is Guid sourceId && ticket.CorrelationId?.StartsWith(
                    $"{ManagerWorkRecovery.Prefix}{sourceId:N}:", StringComparison.Ordinal) == true)
            {
                var source = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(t =>
                    t.Id == sourceId && t.OrganizationId == incident.OrganizationId, token);
                if (source is not null && db.HealthProjectForTask(source) == incident.WorkstreamId)
                    ticket = source;
            }
            var linked = CSweetDbContext.ReadGuidProperty(ticket.PersonalWorkContextJson, "coordinationSessionId");
            var board = CSweetDbContext.ReadGuidProperty(ticket.PersonalWorkContextJson, "boardId");
            string? fingerprint = null;
            try
            {
                using var json = JsonDocument.Parse(ticket.PersonalWorkContextJson ?? "{}");
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("sourceFingerprint", out var f) && f.ValueKind == JsonValueKind.String)
                    fingerprint = f.GetString();
            }
            catch (JsonException) { /* Older work may carry only the direct source work-item identity. */ }
            sessions = sessions.Where(s => s.Id == linked || s.SourceWorkItemId == ticket.Id ||
                (board != null && fingerprint != null && fingerprint != "" && s.SourceBoardId == board && s.InitiatorOrganizationUserId == ticket.AssignedEmployeeId &&
                 s.Turns.Any(t => t.Ordinal == 0 && t.ArtifactKey == fingerprint && t.SpeakerOrganizationUserId == s.InitiatorOrganizationUserId)));
        }
        else return [];
        var sources = await sessions.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Id).Take(6)
            .Select(s => new { s.Id, s.SourceKind, s.Subject, s.Status, s.FinalSummary, s.UpdatedAt, s.ConversationId, s.InitiatorOrganizationUserId,
                Turn = s.Turns.Where(t => (s.SourceKind == "Board" || s.SourceKind == "WorkItem") &&
                    t.Disposition == "Blocked").OrderByDescending(t => t.Ordinal)
                    .Select(t => new { t.Id, t.Ordinal, t.Content }).FirstOrDefault() }).ToArrayAsync(token);
        var facts = sources.Select(s => new ProjectDiagnosticEvidence("StoppedCollaboration", s.Id, s.UpdatedAt,
            ProjectHealthReader.Safe($"{s.Subject} ({s.Status}). " +
                (s.Status != AgentCoordinationStatus.Cancelled && s.Turn is { } t ? $"Turn {t.Ordinal} ({t.Id:D}): {t.Content}" :
                    s.FinalSummary ?? "The session gives no reason.")),
            $"/organizations/{incident.OrganizationId}/communications/{s.ConversationId}?perspective={s.InitiatorOrganizationUserId}&collaboration={s.Id}")).ToList();
        // A terminal summary can say only "blocked". Keep the last substantive statements
        // from the people in that session so a manager can explain what each proposed.
        foreach (var session in sources.Where(s => s.SourceKind is "Board" or "WorkItem"))
        {
            var turns = await (from turn in db.AgentCoordinationTurns.AsNoTracking()
                join speaker in db.CoreOrganizationUsers on turn.SpeakerOrganizationUserId equals speaker.Id
                where turn.SessionId == session.Id && speaker.OrganizationId == incident.OrganizationId &&
                    turn.Disposition == "Continue" && turn.Content != ""
                orderby turn.Ordinal descending
                select new { turn.Id, turn.Ordinal, turn.CreatedAt, turn.Content, speaker.DisplayName, SpeakerId = speaker.Id })
                .Take(6).ToArrayAsync(token);
            var link = facts.First(f => f.SourceId == session.Id).Link;
            foreach (var statement in turns.DistinctBy(t => t.SpeakerId).Take(2))
                facts.Add(new("CollaborationTurn", statement.Id, statement.CreatedAt,
                    ProjectHealthReader.Safe($"{statement.DisplayName}, {session.Subject}, turn {statement.Ordinal}. {statement.Content}"), link));
        }
        return facts.ToArray();
    }
}
