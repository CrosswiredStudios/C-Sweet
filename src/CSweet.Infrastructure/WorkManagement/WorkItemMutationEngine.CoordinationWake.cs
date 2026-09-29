using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    // A terminal collaboration is a wake hint, never delivery evidence or a new execution grant.
    // The caller saves this transition and its outbox entry with the session completion.
    internal async Task WakeCoordinationWaitsAsync(AgentCoordinationSession session, CancellationToken token)
    {
        if (session.Status is not (AgentCoordinationStatus.Completed or AgentCoordinationStatus.Blocked) ||
            session.CompletedAt is null || session.SourceBoardId is not { } sourceBoard) return;
        var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.Id == session.InitiatorOrganizationUserId && x.OrganizationId == session.OrganizationId &&
            x.AgentInstallationId == session.InitiatorInstallationId && x.IsActive && x.ArchivedAt == null, token);
        if (owner is null) return;
        var waiting = await db.CoreWorkTasks.Include(x => x.Board).Where(x =>
            x.OrganizationId == session.OrganizationId && x.AssignedEmployeeId == owner.Id &&
            x.AssignedAgentInstallationId == session.InitiatorInstallationId && x.ArchivedAt == null &&
            x.Status == WorkTaskStatus.Running && x.NextReviewAt != null && x.ClaimEventId == null &&
            x.UpdatedAt < session.CompletedAt && x.PersonalWorkContextJson != null &&
            (x.WaitingOnOrganizationUserId == null || x.WaitingOnOrganizationUserId == session.TargetOrganizationUserId) &&
            x.Board != null && x.Board.ArchivedAt == null &&
            (x.Board.Kind == WorkBoardKind.Personal || x.Board.WorkstreamId != null))
            .OrderBy(x => x.UpdatedAt).Take(HardOpenItemLimit).ToListAsync(token);
        // A ticket-specific commitment may ask for board-wide planning. Correlate that
        // terminal wake to the initiator's exact artifact key, never to arbitrary same-board work.
        var boardFingerprints = session.SourceKind == "Board" && session.SourceWorkItemId is null && waiting.Count > 0
            ? (await db.AgentCoordinationTurns.AsNoTracking().Where(x => x.SessionId == session.Id &&
                x.SpeakerOrganizationUserId == session.InitiatorOrganizationUserId && x.ArtifactKey != null)
                .OrderBy(x => x.Ordinal).Take(32).Select(x => x.ArtifactKey!).ToListAsync(token)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in waiting)
        {
            if (item.Status != WorkTaskStatus.Running || item.NextReviewAt is null || item.ClaimEventId is not null) continue;
            Wire.PersonalTodoWorkContext? context;
            try { context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(item.PersonalWorkContextJson!, JsonOptions); }
            catch (JsonException) { continue; }
            if (context?.BoardId != sourceBoard ||
                context.CoordinationSessionId is { } exact && exact != session.Id ||
                context.WorkstreamId is { } project && session.WorkstreamId != project ||
                context.TeamId is { } team && session.TeamId != team ||
                context.WorkItemId is { } work && session.SourceWorkItemId != work &&
                !(session.SourceWorkItemId is null && !string.IsNullOrWhiteSpace(context.SourceFingerprint) &&
                    boardFingerprints.Contains(context.SourceFingerprint))) continue;
            var todoColumn = await db.WorkBoardColumns.Where(x => x.BoardId == item.BoardId &&
                x.Category == WorkBoardColumnCategory.ToDo).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            if (todoColumn is null) continue;
            var now = clock.GetUtcNow();
            item.Status = WorkTaskStatus.Ready; item.BoardColumnId = todoColumn;
            item.NextReviewAt = null; item.WaitingReason = null; item.WaitingOnOrganizationUserId = null;
            item.ClaimExpiresAt = null; item.UpdatedAt = now; item.Revision++;
            await QueueAvailableAsync(item.OrganizationId, owner, item.BoardId!.Value, item.Id, now, token);
        }
    }

    internal async Task RecoverCoordinationWaitsAsync(CancellationToken token)
    {
        // Bounded reconnect discovery repairs a missed notification or a pre-upgrade terminal session.
        var oldest = await db.CoreWorkTasks.Where(x => x.Status == WorkTaskStatus.Running &&
            x.NextReviewAt != null && x.ClaimEventId == null && x.PersonalWorkContextJson != null && x.ArchivedAt == null)
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MinAsync(token);
        if (oldest is null) return;
        var sessions = await db.AgentCoordinationSessions.AsNoTracking().Where(x =>
            x.CompletedAt > oldest && x.SourceBoardId != null &&
            (x.Status == AgentCoordinationStatus.Completed || x.Status == AgentCoordinationStatus.Blocked))
            .OrderByDescending(x => x.CompletedAt).Take(128).ToListAsync(token);
        foreach (var session in sessions) await WakeCoordinationWaitsAsync(session, token);
    }
}
