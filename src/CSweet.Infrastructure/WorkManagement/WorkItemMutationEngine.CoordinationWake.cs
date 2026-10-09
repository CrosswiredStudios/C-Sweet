using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    private const string CoordinationWakeReceiptAction = "personal-todo.coordination-wake";
    // A terminal collaboration is a wake hint, never delivery evidence or a new execution grant.
    // The caller saves this transition and its outbox entry with the session completion.
    internal async Task WakeCoordinationWaitsAsync(AgentCoordinationSession session, CancellationToken token, WorkTask? deferred = null)
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
            x.CreatedAt < session.CompletedAt && x.PersonalWorkContextJson != null &&
            (x.WaitingOnOrganizationUserId == null || x.WaitingOnOrganizationUserId == session.TargetOrganizationUserId) &&
            x.Board != null && x.Board.ArchivedAt == null &&
            (x.Board.Kind == WorkBoardKind.Personal || x.Board.WorkstreamId != null))
            .OrderBy(x => x.UpdatedAt).Take(HardOpenItemLimit).ToListAsync(token);
        // Defer has not saved yet; include its authorized owner task so a terminal
        // dependency and the resulting availability outbox save atomically.
        if (deferred is not null && !waiting.Any(x => x.Id == deferred.Id) &&
            deferred.OrganizationId == session.OrganizationId && deferred.AssignedEmployeeId == owner.Id &&
            deferred.AssignedAgentInstallationId == session.InitiatorInstallationId && deferred.ArchivedAt == null &&
            deferred.CreatedAt < session.CompletedAt && deferred.Board is { ArchivedAt: null } &&
            (deferred.WaitingOnOrganizationUserId == null || deferred.WaitingOnOrganizationUserId == session.TargetOrganizationUserId))
            waiting.Add(deferred);
        // A ticket-specific commitment may ask for board-wide planning. Correlate that
        // terminal wake to the initiator's exact artifact key, never to arbitrary same-board work.
        var boardFingerprints = session.SourceKind == "Board" && waiting.Count > 0
            ? (await db.AgentCoordinationTurns.AsNoTracking().Where(x => x.SessionId == session.Id &&
                x.SpeakerOrganizationUserId == session.InitiatorOrganizationUserId && x.ArtifactKey != null)
                .OrderBy(x => x.Ordinal).Take(32).Select(x => x.ArtifactKey!).ToListAsync(token)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (session.SourceKind == "Board" && waiting.Count > 0)
        {
            var requests = await db.AgentCoordinationTurns.AsNoTracking().Where(x => x.SessionId == session.Id &&
                x.SpeakerOrganizationUserId == session.InitiatorOrganizationUserId &&
                (x.ArtifactType == "video-game.production.role-estimate-request.v1" ||
                 x.ArtifactType == "video-game.production.qa-readiness-request.v1"))
                .OrderBy(x => x.Ordinal).Take(32).ToListAsync(token);
            foreach (var request in requests)
                if (ReadPlanningDigest(request) is { } digest) boardFingerprints.Add(digest);
        }
        foreach (var item in waiting)
        {
            if (item.Status != WorkTaskStatus.Running || item.NextReviewAt is null || item.ClaimEventId is not null) continue;
            await BindCoordinationWaitAsync(item, token);
            Wire.PersonalTodoWorkContext? context;
            try { context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(item.PersonalWorkContextJson!, JsonOptions); }
            catch (JsonException) { continue; }
            // A specialist can finish before the owner releases its claim or saves
            // the wait. Exact cycle provenance makes that late deferral recoverable.
            var exactCycle = !string.IsNullOrWhiteSpace(context?.SourceFingerprint) &&
                boardFingerprints.Contains(context.SourceFingerprint);
            if (item.UpdatedAt >= session.CompletedAt && !exactCycle) continue;
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
            // A cycle can await several role authorities. Once one completion has
            // woken reconciliation, it must not wake the next wait for another role.
            // PostgreSQL timestamps retain microseconds; keep the key identical
            // before persistence and after reconnect reads.
            var receiptKey = $"{item.Id:N}:{session.Id:N}:{session.CompletedAt.Value.UtcTicks / 10}";
            if (db.WorkItemMutationReceipts.Local.Any(x => x.OrganizationId == item.OrganizationId &&
                    x.AgentInstallationId == session.InitiatorInstallationId && x.Action == CoordinationWakeReceiptAction && x.IdempotencyKey == receiptKey) ||
                await db.WorkItemMutationReceipts.AsNoTracking().AnyAsync(x => x.OrganizationId == item.OrganizationId &&
                    x.AgentInstallationId == session.InitiatorInstallationId && x.Action == CoordinationWakeReceiptAction && x.IdempotencyKey == receiptKey, token)) continue;
            var now = clock.GetUtcNow();
            item.Status = WorkTaskStatus.Ready; item.BoardColumnId = todoColumn;
            item.NextReviewAt = null; item.WaitingReason = null; item.WaitingOnOrganizationUserId = null;
            item.ClaimExpiresAt = null; item.UpdatedAt = now; item.Revision++;
            db.WorkItemMutationReceipts.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, AgentInstallationId = session.InitiatorInstallationId,
                Action = CoordinationWakeReceiptAction, IdempotencyKey = receiptKey, ResourceId = item.Id, CreatedAt = now,
                ResultJson = JsonSerializer.Serialize(new { coordinationSessionId = session.Id, completedAt = session.CompletedAt, revision = item.Revision }, JsonOptions)
            });
            await QueueAvailableAsync(item.OrganizationId, owner, item.BoardId!.Value, item.Id, now, token);
        }
    }

    internal async Task BindCoordinationWaitAsync(WorkTask item, CancellationToken token)
    {
        Wire.PersonalTodoWorkContext? context;
        try { context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(item.PersonalWorkContextJson ?? "null", JsonOptions); }
        catch (JsonException) { return; }
        if (context?.WorkstreamId is not { } project || string.IsNullOrWhiteSpace(context.SourceFingerprint) ||
            item.AssignedEmployeeId is not { } owner || item.AssignedAgentInstallationId is not { } installation) return;
        // Upgrade only a pre-board context or a context still attached to its terminal
        // intake handoff. Never replace an explicitly awaited board session.
        if (context.CoordinationSessionId is { } oldId)
        {
            var old = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == oldId && x.OrganizationId == item.OrganizationId &&
                (x.InitiatorInstallationId == installation || x.TargetInstallationId == installation), token);
            if (old is null || old.SourceBoardId != null || old.Status != AgentCoordinationStatus.Completed) return;
        }
        else if (context.BoardId is not null) return;
        var sessions = await db.AgentCoordinationSessions.AsNoTracking().Where(x =>
            x.OrganizationId == item.OrganizationId && x.InitiatorOrganizationUserId == owner &&
            x.InitiatorInstallationId == installation && x.SourceKind == "Board" && x.SourceBoardId != null &&
            (x.Status == AgentCoordinationStatus.Active || x.Status == AgentCoordinationStatus.Summarizing ||
                x.Status == AgentCoordinationStatus.Completed || x.Status == AgentCoordinationStatus.Blocked) &&
            x.WorkstreamId == project && x.TeamId == context.TeamId &&
            (context.BoardId == null || x.SourceBoardId == context.BoardId) &&
            (x.SourceWorkItemId == null || x.SourceWorkItemId == context.WorkItemId) &&
            db.AgentCoordinationTurns.Any(t => t.SessionId == x.Id &&
                t.SpeakerOrganizationUserId == owner && (t.ArtifactKey == context.SourceFingerprint ||
                    ((t.ArtifactType == "video-game.production.role-estimate-request.v1" ||
                      t.ArtifactType == "video-game.production.qa-readiness-request.v1") &&
                     t.ArtifactPayloadJson != null && t.ArtifactPayloadJson.Contains(context.SourceFingerprint)))))
            .OrderByDescending(x => x.CreatedAt).Take(33).ToListAsync(token);
        if (sessions.Count > 32) return;
        // SQL narrows discovery; parsed equality, not substring matching, binds the cycle.
        var ids = sessions.Select(x => x.Id).ToArray();
        var evidence = await db.AgentCoordinationTurns.AsNoTracking().Where(x => ids.Contains(x.SessionId) &&
            x.SpeakerOrganizationUserId == owner).OrderBy(x => x.Ordinal).Take(1056).ToListAsync(token);
        sessions = sessions.Where(s => evidence.Any(t => t.SessionId == s.Id &&
            (t.ArtifactKey == context.SourceFingerprint || ReadPlanningDigest(t) == context.SourceFingerprint))).ToList();
        if (sessions.Count is 0 or > 32 || sessions.Select(x => x.SourceBoardId).Distinct().Count() != 1) return;
        item.PersonalWorkContextJson = JsonSerializer.Serialize(context with
        {
            BoardId = sessions[0].SourceBoardId,
            // Multiple role proposals share the cycle fingerprint; either may wake
            // reconciliation, which always re-reads all required proposals.
            CoordinationSessionId = null
        }, JsonOptions);
    }

    private static string? ReadPlanningDigest(AgentCoordinationTurn turn)
    {
        if (turn.ArtifactType is not ("video-game.production.role-estimate-request.v1" or
            "video-game.production.qa-readiness-request.v1") || turn.ArtifactPayloadJson is null) return null;
        try
        {
            using var document = JsonDocument.Parse(turn.ArtifactPayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if ((document.RootElement.TryGetProperty("PlanningDigest", out var value) ||
                 document.RootElement.TryGetProperty("planningDigest", out value)) &&
                value.ValueKind == JsonValueKind.String) return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    internal async Task RecoverCoordinationWaitsAsync(CancellationToken token, WorkTask? deferred = null)
    {
        if (deferred is not null)
        {
            // A new wait needs only its owner's cycle; do not scan the entire
            // organization's recovery backlog on every deferral.
            await BindCoordinationWaitAsync(deferred, token);
            Wire.PersonalTodoWorkContext? context;
            try { context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(deferred.PersonalWorkContextJson ?? "null", JsonOptions); }
            catch (JsonException) { return; }
            if (context?.BoardId is not { } board) return;
            var completed = await db.AgentCoordinationSessions.AsNoTracking().Where(x =>
                x.OrganizationId == deferred.OrganizationId && x.InitiatorOrganizationUserId == deferred.AssignedEmployeeId &&
                x.InitiatorInstallationId == deferred.AssignedAgentInstallationId && x.SourceBoardId == board &&
                x.CompletedAt > deferred.CreatedAt &&
                (x.Status == AgentCoordinationStatus.Completed || x.Status == AgentCoordinationStatus.Blocked))
                .OrderByDescending(x => x.CompletedAt).Take(128).ToListAsync(token);
            foreach (var session in completed) await WakeCoordinationWaitsAsync(session, token, deferred);
            return;
        }
        // Bounded reconnect discovery repairs a missed notification or a pre-upgrade terminal session.
        var oldest = await db.CoreWorkTasks.Where(x => x.Status == WorkTaskStatus.Running &&
            x.NextReviewAt != null && x.ClaimEventId == null && x.PersonalWorkContextJson != null && x.ArchivedAt == null)
            .Select(x => (DateTimeOffset?)x.CreatedAt).MinAsync(token);
        if (oldest is null) return;
        var sessions = await db.AgentCoordinationSessions.AsNoTracking().Where(x =>
            x.CompletedAt > oldest && x.SourceBoardId != null &&
            (x.Status == AgentCoordinationStatus.Completed || x.Status == AgentCoordinationStatus.Blocked))
            .OrderByDescending(x => x.CompletedAt).Take(128).ToListAsync(token);
        var waits = await db.CoreWorkTasks.Where(x => x.Status == WorkTaskStatus.Running &&
            x.NextReviewAt != null && x.ClaimEventId == null && x.PersonalWorkContextJson != null && x.ArchivedAt == null)
            .OrderBy(x => x.UpdatedAt).Take(HardOpenItemLimit).ToListAsync(token);
        foreach (var item in waits) await BindCoordinationWaitAsync(item, token);
        foreach (var session in sessions) await WakeCoordinationWaitsAsync(session, token);
    }
}
