using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    // Recovery is driven by current correlated state. A wake or a requeue never supplies
    // project scope, an approval, or new authority. Discovery is bounded and exact.
    private async Task<List<AgentCoordinationSession>> CommitmentCollaborationsAsync(WorkTask item, CancellationToken token)
    {
        if (item.AssignedEmployeeId is not { } owner || item.AssignedAgentInstallationId is not { } installation)
            return [];
        await BindCoordinationWaitAsync(item, token);
        Wire.PersonalTodoWorkContext? context;
        try { context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(item.PersonalWorkContextJson ?? "null", JsonOptions); }
        catch (JsonException) { return []; }
        if (context?.BoardId is not { } board ||
            context.CoordinationSessionId is null && string.IsNullOrWhiteSpace(context.SourceFingerprint)) return [];
        var sessions = await db.AgentCoordinationSessions.Include(x => x.Turns).Where(x =>
            x.OrganizationId == item.OrganizationId && x.SourceKind == "Board" && x.SourceBoardId == board &&
            x.InitiatorOrganizationUserId == owner && x.InitiatorInstallationId == installation &&
            (context.WorkstreamId == null || x.WorkstreamId == context.WorkstreamId) &&
            (context.TeamId == null || x.TeamId == context.TeamId) &&
            (x.SourceWorkItemId == null || x.SourceWorkItemId == context.WorkItemId) &&
            (context.CoordinationSessionId == null || x.Id == context.CoordinationSessionId) &&
            (x.Status == AgentCoordinationStatus.Blocked || x.Status == AgentCoordinationStatus.Active ||
             x.Status == AgentCoordinationStatus.Summarizing || x.Status == AgentCoordinationStatus.Completed))
            .OrderByDescending(x => x.UpdatedAt).Take(33).ToListAsync(token);
        // Avoid choosing an arbitrary subset of a large/ambiguous recovery backlog.
        if (sessions.Count > 32) return [];
        return sessions.Where(x => context.CoordinationSessionId == x.Id ||
            x.Turns.Any(t => t.SpeakerOrganizationUserId == owner &&
                (t.ArtifactKey == context.SourceFingerprint || ReadPlanningDigest(t) == context.SourceFingerprint))).ToList();
    }

    private static bool NeedsFreshBoardResponse(AgentCoordinationSession session) =>
        session.Status == AgentCoordinationStatus.Blocked &&
        session.Turns.Where(x => x.SpeakerOrganizationUserId == session.TargetOrganizationUserId)
            .OrderByDescending(x => x.Ordinal).FirstOrDefault() is { Disposition: "Blocked", ArtifactType: null };

    private async Task<bool> ResumeCommitmentSessionAsync(WorkTask item, AgentCoordinationSession session,
        string recoveryKey, CancellationToken token)
    {
        if (!NeedsFreshBoardResponse(session)) return false;
        var coordination = Coordination;
        if (coordination is null) return false;
        var installation = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == session.TargetInstallationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token);
        if (installation is null) return false;
        foreach (var (userId, installationId) in new[]
        {
            (session.InitiatorOrganizationUserId, session.InitiatorInstallationId),
            (session.TargetOrganizationUserId, session.TargetInstallationId)
        })
        {
            if (!await db.CoreOrganizationUsers.AnyAsync(x => x.Id == userId && x.OrganizationId == item.OrganizationId &&
                x.IsActive && x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId == installationId, token)) return false;
            var json = await db.AgentInstallationGrants.Where(x => x.AgentInstallationId == installationId)
                .Select(x => x.RequiredCapabilitiesJson).SingleOrDefaultAsync(token);
            string[] capabilities;
            try { capabilities = JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; }
            catch (JsonException) { return false; }
            if (!capabilities.Contains(CSweet.Agent.SDK.CommunicationCapabilities.CoordinationRead) ||
                !capabilities.Contains(CSweet.Agent.SDK.CommunicationCapabilities.CoordinationRespond)) return false;
        }
        await RecordCommitmentDiscussionAsync(item, session, token);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recoveryKey))).ToLowerInvariant();
        await coordination.ResumeAsync(item.OrganizationId, session.InitiatorOrganizationUserId,
            session.InitiatorInstallationId, new CSweet.Agent.SDK.ResumeAgentCoordinationRequest(
                session.Id, session.Revision,
                "The owning commitment is being retried. Re-read current authorized inputs and produce a fresh response; prior blockers and deferred checks are not approvals. Explain the issue to your counterpart, propose a bounded remedy, and use existing authorized work-item capabilities to create or reuse actionable repair work when appropriate. Report the ticket references, owner, verification needed, and any decision still required. Do not repeat an unchanged failed attempt indefinitely.",
                $"commitment-recovery:{item.Id:N}:{session.Id:N}:{digest}"), token);
        db.WorkItemComments.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, WorkItemId = item.Id,
            AuthorKind = GrantSubjectKind.AutomationIdentity, AuthorSubjectId = session.Id,
            AuthorDisplayName = "C-Sweet recovery", Kind = "coordination.recovery",
            CoordinationSessionId = session.Id, IdempotencyKey = $"commitment-retry:{item.Id:N}:{session.Id:N}:{digest}",
            Body = $"A fresh response was requested from the specialist for this commitment. Review the [collaboration](/organizations/{item.OrganizationId:D}/communications/{session.ConversationId:D}). Next step for the owning agent: discuss the cause and a bounded remedy with the specialist; create or reuse authorized repair work where appropriate, and report its owner, ticket references, verification and remaining decisions. An unchanged failure must remain visible as blocked.",
            CreatedAt = clock.GetUtcNow()
        });
        return true;
    }

    internal async Task RecordCoordinationDiscussionAsync(AgentCoordinationSession session, CancellationToken token)
    {
        if (session.SourceKind != "Board") return;
        var candidates = await db.CoreWorkTasks.Include(x => x.Board).Where(x =>
            x.OrganizationId == session.OrganizationId && x.AssignedEmployeeId == session.InitiatorOrganizationUserId &&
            x.AssignedAgentInstallationId == session.InitiatorInstallationId && x.ArchivedAt == null &&
            x.PersonalWorkContextJson != null && x.Board != null && x.Board.ArchivedAt == null &&
            (x.Board.Kind == WorkBoardKind.Personal || x.Board.WorkstreamId != null))
            .OrderByDescending(x => x.UpdatedAt).Take(HardOpenItemLimit).ToListAsync(token);
        foreach (var item in candidates)
            if ((await CommitmentCollaborationsAsync(item, token)).Any(x => x.Id == session.Id))
                await RecordCommitmentDiscussionAsync(item, session, token);
    }

    private async Task ResumeBlockedCommitmentCoordinationAsync(WorkTask item, string recoveryKey, CancellationToken token)
    {
        if (item.Status != WorkTaskStatus.Blocked || Coordination is null) return;
        foreach (var session in await CommitmentCollaborationsAsync(item, token))
            await ResumeCommitmentSessionAsync(item, session, recoveryKey, token);
    }

    private async Task RecoverBlockedCoordinationCommitmentsAsync(DateTimeOffset now, CancellationToken token)
    {
        if (Coordination is null) return;
        var blocked = await db.CoreWorkTasks.Include(x => x.Board).Where(x =>
            (x.Status == WorkTaskStatus.Blocked || x.Status == WorkTaskStatus.Running || x.Status == WorkTaskStatus.Ready) && x.IsExecutable && x.ArchivedAt == null &&
            x.PersonalWorkContextJson != null && x.Board != null && x.Board.ArchivedAt == null &&
            (x.Board.Kind == WorkBoardKind.Personal || x.Board.WorkstreamId != null))
            .OrderBy(x => x.UpdatedAt).Take(HardOpenItemLimit).ToListAsync(token);
        foreach (var item in blocked)
        {
            var sessions = await CommitmentCollaborationsAsync(item, token);
            foreach (var session in sessions)
                await RecordCommitmentDiscussionAsync(item, session, token);
            if (item.Status != WorkTaskStatus.Blocked) continue;
            var changed = sessions.Any(x => x.UpdatedAt > item.UpdatedAt && x.Status is
                AgentCoordinationStatus.Active or AgentCoordinationStatus.Summarizing or AgentCoordinationStatus.Completed);
            var updated = new List<AgentCoordinationSession>();
            foreach (var session in sessions.Where(NeedsFreshBoardResponse))
                if (session.CompletedAt is { } ended && await db.AgentInstallations.AsNoTracking().AnyAsync(x =>
                    x.Id == session.TargetInstallationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
                    x.UpdatedAt > ended, token)) updated.Add(session);
            if (!changed && updated.Count == 0) continue;
            var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == item.AssignedEmployeeId &&
                x.OrganizationId == item.OrganizationId && x.IsActive && x.AgentInstallationId == item.AssignedAgentInstallationId, token);
            if (owner is null) continue;
            await using var transaction = db.Database.CurrentTransaction is null && db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(token) : null;
            var resumed = false;
            foreach (var session in updated)
                resumed |= await ResumeCommitmentSessionAsync(item, session, $"installation-update:{session.Revision}", token);
            if (!changed && !resumed) continue;
            var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == item.BoardId, token);
            item.Status = WorkTaskStatus.Ready; item.BoardColumnId = ColumnForStatus(board, WorkTaskStatus.Ready).Id;
            item.BlockReason = null; item.ClaimEventId = null; item.ClaimExpiresAt = null;
            item.NextReviewAt = null; item.WaitingReason = null; item.WaitingOnOrganizationUserId = null;
            item.Revision++; item.UpdatedAt = now;
            await ReopenBlockedPlanChildrenAsync(item, token);
            await QueueAvailableAsync(item.OrganizationId, owner, board.Id, item.Id, now, token);
            await SaveChangesWithRealtimeAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
        }
    }

    private async Task RecordCommitmentDiscussionAsync(WorkTask item, AgentCoordinationSession session, CancellationToken token)
    {
        // A platform projection of actual authenticated turns, never invented agent speech.
        // The exact owning commitment's board grants govern who can read this discussion.
        var existing = (await db.WorkItemComments.Where(x => x.WorkItemId == item.Id &&
            x.CoordinationSessionId == session.Id).Select(x => x.IdempotencyKey).ToListAsync(token)).ToHashSet();
        existing.UnionWith(db.WorkItemComments.Local.Where(x => x.WorkItemId == item.Id &&
            x.CoordinationSessionId == session.Id).Select(x => x.IdempotencyKey));
        var added = false;
        foreach (var turn in session.Turns.OrderBy(x => x.Ordinal))
        {
            var key = $"commitment-discussion:{item.Id:N}:{turn.Id:N}";
            if (!existing.Add(key)) continue;
            added = true;
            var speaker = await db.CoreOrganizationUsers.Where(x => x.Id == turn.SpeakerOrganizationUserId &&
                x.OrganizationId == item.OrganizationId).Select(x => x.DisplayName).SingleOrDefaultAsync(token) ?? "Agent";
            var body = $"{speaker} · {turn.Disposition}\n{turn.Content}";
            if (body.Length > 7800) body = body[..7800] + "\nResponse excerpt; full text is retained in the collaboration.";
            body += $"\n\n[Open collaboration](/organizations/{item.OrganizationId:D}/communications/{session.ConversationId:D})";
            db.WorkItemComments.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, WorkItemId = item.Id,
                AuthorKind = GrantSubjectKind.AutomationIdentity, AuthorSubjectId = session.Id,
                AuthorDisplayName = "C-Sweet collaboration transcript", Body = body,
                Kind = "coordination.transcript", CoordinationSessionId = session.Id,
                CausationId = turn.Id.ToString("D"), ArtifactDigest = turn.ArtifactDigest,
                IdempotencyKey = key, CreatedAt = turn.CreatedAt
            });
        }
        if (added) await QueuePersonalRealtimeAsync(item, token);
    }
}
