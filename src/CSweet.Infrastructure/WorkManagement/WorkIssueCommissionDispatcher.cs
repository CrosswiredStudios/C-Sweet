using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkIssueCommissionDispatcher(CSweetDbContext db, WorkIssueService issues, IAgentCoordinationService coordination,
    IPersonalTodoService todos, TimeProvider clock)
{
    private async Task<string> AuthorityBasisAsync(CSweet.Domain.WorkManagement.WorkIssueCommission row, CancellationToken ct)
    {
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == row.BoardId && b.OrganizationId == row.OrganizationId, ct);
        var source = WorkIssueService.Read<Wire.WorkApprovedSource>(row.SourceJson);
        var boardManager = board?.ManagerOrganizationUserId; var boardTeam = board?.TeamId;
        var related = new HashSet<Guid> { row.InitiatorEmployeeId, row.TargetEmployeeId };
        var cursor = boardManager; var seen = new HashSet<Guid>();
        while (cursor is Guid person && seen.Add(person))
        {
            related.Add(person);
            cursor = await db.CoreOrganizationUsers.AsNoTracking().Where(p => p.OrganizationId == row.OrganizationId && p.Id == person)
                .Select(p => p.ReportsToOrganizationUserId).SingleOrDefaultAsync(ct);
        }
        var relatedIds = related.ToArray();
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(p => p.OrganizationId == row.OrganizationId &&
            relatedIds.Contains(p.Id))
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.AgentInstallationId, p.IsActive, p.ArchivedAt, p.ReportsToOrganizationUserId }).ToArrayAsync(ct);
        var subjects = people.Select(p => p.AgentInstallationId ?? p.Id).ToArray();
        var grants = await db.ScopedActionGrants.AsNoTracking().Where(g => g.OrganizationId == row.OrganizationId && subjects.Contains(g.SubjectId) &&
            (g.ScopeKind == CSweet.Domain.Security.GrantScopeKind.Organization || g.ScopeId == row.BoardId || g.ScopeId == boardTeam || g.ScopeId == source.DocumentId))
            .OrderBy(g => g.Id).Select(g => new { g.Id, g.Revision, g.RevokedAt, g.ExpiresAt }).ToArrayAsync(ct);
        var installations = await db.AgentInstallations.AsNoTracking().Where(i => subjects.Contains(i.Id)).OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.PackageVersionId, i.IsEnabled, i.RevisionStatus, i.SetupState, grantRevision = i.Grant == null ? (long?)null : i.Grant.GrantRevision }).ToArrayAsync(ct);
        var document = await db.CoreArtifacts.AsNoTracking().Where(a => a.Id == source.DocumentId && a.OrganizationId == row.OrganizationId)
            .Select(a => a.AcceptedRevisionId).SingleOrDefaultAsync(ct);
        return MutationContent.Digest(new { policy = "issue-commission-authority.v1", row.InitiatorInstallationId, people, grants, installations,
            board = board == null ? null : new { board.Id, board.TeamId, board.ManagerOrganizationUserId, board.ArchivedAt }, document, source });
    }
    public async Task DispatchAsync(CancellationToken ct)
    {
        // Platform discovery is bounded and current-state based. A denied commission is retried only
        // once per relevant authority basis; it does not become permanently undiscoverable.
        var parked = await db.WorkIssueCommissions.Where(x => x.Status == "NeedsAuthority" && x.NextAttemptAt <= clock.GetUtcNow())
            .OrderBy(x => x.NextAttemptAt).Take(50).ToListAsync(ct);
        foreach (var row in parked)
        {
            var basis = await AuthorityBasisAsync(row, ct);
            var key = row.Id.ToString("N") + ":" + basis;
            if (!await db.DurableMutationReceipts.AnyAsync(r => r.OrganizationId == row.OrganizationId && r.PrincipalId == row.InitiatorEmployeeId &&
                r.ResourceKey == "issue-commission-authority" && r.MutationKey == key, ct))
            {
                row.Status = row.SessionId is null ? "Pending" : "Dispatched"; row.Revision++;
                row.NextAttemptAt = clock.GetUtcNow();
                continue;
            }
            row.NextAttemptAt = clock.GetUtcNow().AddMinutes(1);
        }
        await db.SaveChangesAsync(ct);

        var pending = await db.WorkIssueCommissions.Where(x => (x.Status == "Pending" || x.Status == "Dispatched" || x.Status == "RetirementPending") && x.NextAttemptAt <= clock.GetUtcNow())
            .OrderBy(x => x.NextAttemptAt).Take(50).ToListAsync(ct);
        foreach (var row in pending)
        {
            try { await DispatchOneAsync(row.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException or DbUpdateException)
            {
                // A rolled-back dispatch may have tracked session/card writes. Never save those
                // incidentally while recording the failure outside that transaction.
                var id = row.Id;
                db.ChangeTracker.Clear();
                var failed = await db.WorkIssueCommissions.SingleAsync(x => x.Id == id, ct);
                failed.Failure = ex.Message; failed.NextAttemptAt = clock.GetUtcNow().AddMinutes(1); failed.Revision++;
                var transient = ex is DbUpdateConcurrencyException || ex is DbUpdateException { InnerException: Npgsql.NpgsqlException { IsTransient: true } };
                if (!transient)
                {
                    failed.Status = ex is UnauthorizedAccessException ? "NeedsAuthority" : "NeedsReview";
                    var board = await db.WorkBoards.SingleOrDefaultAsync(b => b.Id == failed.BoardId && b.OrganizationId == failed.OrganizationId && b.ArchivedAt == null, ct);
                    if (board is not null)
                    {
                        var owner = board.ManagerOrganizationUserId ?? failed.InitiatorEmployeeId;
                        var ids = WorkIssueService.Read<Guid[]>(failed.IssueIdsJson);
                        foreach (var issue in await db.WorkIssueCases.Where(i => ids.Contains(i.Id) && i.OrganizationId == failed.OrganizationId && i.Status == "Open").ToListAsync(ct))
                        { issue.Phase = "AwaitingReview"; issue.AccountableEmployeeId = owner; issue.NextAction = "Review the retained commission failure and current authority or source before resuming"; issue.Revision++; issue.UpdatedAt = clock.GetUtcNow(); }
                        if (await db.CoreOrganizationUsers.AnyAsync(p => p.OrganizationId == failed.OrganizationId && p.Id == owner && p.IsActive, ct))
                            await issues.QueueChangedAsync(failed.OrganizationId, board, owner, ct);
                    }
                }
                if (failed.Status == "NeedsAuthority")
                {
                    var basis = await AuthorityBasisAsync(failed, ct); var key = failed.Id.ToString("N") + ":" + basis;
                    if (!await db.DurableMutationReceipts.AnyAsync(r => r.OrganizationId == failed.OrganizationId && r.PrincipalId == failed.InitiatorEmployeeId && r.ResourceKey == "issue-commission-authority" && r.MutationKey == key, ct))
                        db.DurableMutationReceipts.Add(new() { Id = Guid.NewGuid(), OrganizationId = failed.OrganizationId, PrincipalId = failed.InitiatorEmployeeId,
                            ResourceKey = "issue-commission-authority", MutationKey = key, ContentDigest = basis, ResultJson = "{}", CreatedAt = clock.GetUtcNow() });
                }
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task DispatchOneAsync(Guid id, CancellationToken ct)
    {
        await using var dispatch = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "WorkIssueCommissions" WHERE "Id" = {id} FOR UPDATE""", ct);
        var row = await db.WorkIssueCommissions.SingleAsync(x => x.Id == id, ct);
        if (dispatch is not null) await db.Entry(row).ReloadAsync(ct);
        if (row.Status is not ("Pending" or "Dispatched" or "RetirementPending")) return;
        var board = await issues.RequireBoardAsync(row.OrganizationId, row.InitiatorEmployeeId, row.BoardId, true, ct);
        var currentInstallation = await db.CoreOrganizationUsers.Where(p => p.OrganizationId == row.OrganizationId && p.Id == row.InitiatorEmployeeId && p.IsActive && p.ArchivedAt == null)
            .Select(p => p.AgentInstallationId).SingleOrDefaultAsync(ct) ?? throw new UnauthorizedAccessException("The commission initiator is unavailable.");
        // The intent belongs to the employee. A published runtime update may replace its installation,
        // but only the current authorized principal can continue it; old grants are never restored.
        row.InitiatorInstallationId = currentInstallation;
        var source = WorkIssueService.Read<Wire.WorkApprovedSource>(row.SourceJson);
        if (!await issues.CanReadSourceAsync(row.OrganizationId, row.InitiatorEmployeeId, source, ct)) throw new UnauthorizedAccessException("The commission owner no longer has source access.");
        await issues.RequireSourceAsync(row.OrganizationId, board, source, ct);
        var memberIds = WorkIssueService.Read<Guid[]>(row.IssueIdsJson);
        var members = await db.WorkIssueCases.Where(x => memberIds.Contains(x.Id) && x.OrganizationId == row.OrganizationId && x.BoardId == board.Id).ToListAsync(ct);
        if (members.Count != memberIds.Length) throw new InvalidOperationException("The commission membership changed.");
        if (row.Status == "RetirementPending")
        {
            if (members.Any(x => x.Status == "Open")) throw new InvalidOperationException("Retirement requires a terminal issue receipt.");
            var retired = await RetireTransferredWorkAsync(row, source, ct, retirement: true);
            row.Status = retired ? "Retired" : "RetirementPending"; row.NextAttemptAt = clock.GetUtcNow().AddSeconds(10); row.Revision++;
            await db.SaveChangesAsync(ct); if (dispatch is not null) await dispatch.CommitAsync(ct); return;
        }
        if (members.All(x => x.Status != "Open") && row.SessionId is null)
        {
            row.Status = "Superseded"; row.Revision++; await db.SaveChangesAsync(ct); if (dispatch is not null) await dispatch.CommitAsync(ct); return;
        }
        if (row.SessionId is null)
        {
            var retainedIds = WorkIssueService.Read<Guid[]>(row.SupersededSessionIdsJson);
            var retainedWorks = db.AgentCoordinationSessions.Where(s => s.OrganizationId == row.OrganizationId && retainedIds.Contains(s.Id) && s.CurrentAgentWorkItemId != null)
                .Select(s => s.CurrentAgentWorkItemId!.Value);
            if (await db.AgentWorkItems.AnyAsync(w => retainedWorks.Contains(w.Id) && w.Status == CSweet.Domain.Setup.AgentWorkStatus.Leased, ct))
            {
                row.NextAttemptAt = clock.GetUtcNow().AddSeconds(10); await db.SaveChangesAsync(ct);
                if (dispatch is not null) await dispatch.CommitAsync(ct); return;
            }
            var key = "issue-commission:" + row.Id.ToString("N");
            var request = new WorkContributionRequest(board.WorkstreamId!.Value, board.Id, key, row.Question, [new(source.DocumentId, source.RevisionId, source.Digest)]);
            var artifact = new AgentCoordinationArtifactSubmission(WorkContributions.RequestType, "1.0", key, 1, true, JsonSerializer.SerializeToElement(request));
            var session = await coordination.StartBoardAsync(row.OrganizationId, row.InitiatorEmployeeId, row.InitiatorInstallationId,
                new(row.TargetEmployeeId, board.Id, "Reconcile work responsibilities", row.Question,
                    ["Explain the meaningful owner of each supplied obligation without weakening requirements or inventing evidence."], row.Question, key, artifact), ct);
            // A crash here replays StartBoardAsync with the same persisted commission identity.
            await todos.AddAsync(row.OrganizationId, new PersonalTodoActor(row.InitiatorEmployeeId, row.InitiatorInstallationId),
                new Wire.AddPersonalTodoItemRequest("Obtain scoped planning advice", "The team is reconciling related responsibilities. Advice will be assessed against the accepted source.", "High", null, key, CorrelationId: TrackedCollaboration.Correlation(session.Id)) {
                    WorkContext = new(WorkstreamId: board.WorkstreamId, TeamId: board.TeamId, BoardId: board.Id, CoordinationSessionId: session.Id, IssueCommissionId: row.Id)
                }, ct);
            await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            row.SessionId = session.Id; row.Status = "Dispatched"; row.Failure = null; row.Revision++;
            foreach (var member in members)
                member.LinksJson = WorkIssueService.Serialize(WorkIssueService.Read<Wire.WorkIssueLink[]>(member.LinksJson).Append(new("Coordination", session.Id)).Distinct().ToArray());
            await issues.QueueChangedAsync(row.OrganizationId, board, row.InitiatorEmployeeId, ct);
            await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        }
        else
        {
            var session = await coordination.ReadAsync(row.OrganizationId, row.InitiatorEmployeeId, row.SessionId.Value, ct)
                ?? throw new InvalidOperationException("The commissioned session is missing; review the durable intent before retrying.");
            if (session.Status is AgentCoordinationStatuses.Completed or AgentCoordinationStatuses.Cancelled or AgentCoordinationStatuses.Failed or AgentCoordinationStatuses.Blocked)
            {
                await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
                row.Status = session.Status == AgentCoordinationStatuses.Completed ? "AdviceReceived" : "NeedsReview";
                row.Failure = row.Status == "NeedsReview" ? session.FinalSummary : null; row.Revision++;
                foreach (var member in members.Where(x => x.Status == "Open")) { member.Phase = "AwaitingReview"; member.Revision++; member.UpdatedAt = clock.GetUtcNow(); }
                await issues.QueueChangedAsync(row.OrganizationId, board, row.InitiatorEmployeeId, ct);
                await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
            }
            else { row.NextAttemptAt = clock.GetUtcNow().AddSeconds(10); await db.SaveChangesAsync(ct); }
        }
        if (row.SessionId.HasValue) await RetireTransferredWorkAsync(row, source, ct);
        if (dispatch is not null) await dispatch.CommitAsync(ct);
    }

    private async Task<bool> RetireTransferredWorkAsync(CSweet.Domain.WorkManagement.WorkIssueCommission row, Wire.WorkApprovedSource source, CancellationToken ct, bool retirement = false)
    {
        var complete = true;
        var memberIds = WorkIssueService.Read<Guid[]>(row.IssueIdsJson);
        var stillNeeded = await db.WorkIssueCases.AsNoTracking().Where(i => i.OrganizationId == row.OrganizationId && i.BoardId == row.BoardId &&
            i.Status == "Open" && (retirement || !memberIds.Contains(i.Id))).ToListAsync(ct);
        foreach (var id in WorkIssueService.Read<Guid[]>(row.SupersededSessionIdsJson))
        {
            if (stillNeeded.Any(i => WorkIssueService.Read<Wire.WorkIssueLink[]>(i.LinksJson).Any(l => l.Kind == "Coordination" && l.Id == id)))
            { complete = false; continue; } // Resolving one member cannot cancel advice still required by another.
            var old = await coordination.ReadAsync(row.OrganizationId, row.InitiatorEmployeeId, id, ct)
                ?? throw new InvalidOperationException("A carried-forward session is missing; review its recorded responsibility before retiring tracking.");
            if (old.BoardSource?.BoardId != row.BoardId || old.Initiator.OrganizationUserId != row.InitiatorEmployeeId || !retirement && old.Target.OrganizationUserId != row.TargetEmployeeId) continue;
            var artifact = old.Turns.FirstOrDefault(t => t.SpeakerOrganizationUserId == row.InitiatorEmployeeId && t.Artifact?.Type == WorkContributions.RequestType)?.Artifact;
            var input = artifact?.Payload.Deserialize<WorkContributionRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (input?.BoardId != row.BoardId || input.Sources is not { Count: 1 } || input.Sources[0] != new WorkContributionSource(source.DocumentId, source.RevisionId, source.Digest)) continue;
            var tracked = await db.AgentCoordinationSessions.SingleAsync(s => s.Id == id && s.OrganizationId == row.OrganizationId, ct);
            // An actively leased turn retains its authority until it reports. Transfer only idle work.
            if (tracked.CurrentAgentWorkItemId is Guid work && await db.AgentWorkItems.AnyAsync(w => w.Id == work && w.Status == CSweet.Domain.Setup.AgentWorkStatus.Leased, ct)) { complete = false; continue; }
            if (old.Status is AgentCoordinationStatuses.Active or AgentCoordinationStatuses.Summarizing)
                await coordination.CancelAsync(row.OrganizationId, row.InitiatorEmployeeId, false,
                    new(id, old.Revision, "Responsibility transferred to durable commission " + row.Id.ToString("D"), "issue-transfer:" + row.Id.ToString("N") + ":" + id.ToString("N")), ct);
            var cards = await todos.ListAsync(row.OrganizationId, new(row.InitiatorEmployeeId, row.InitiatorInstallationId), cancellationToken: ct);
            foreach (var card in cards.Boards.Where(b => b.OwnerOrganizationUserId == row.InitiatorEmployeeId).SelectMany(b => b.Items).Where(c => TrackedCollaboration.IsTrackingCard(c) && c.WorkContext?.CoordinationSessionId == id &&
                c.Status is Wire.PersonalTodoStatuses.Ready or Wire.PersonalTodoStatuses.Blocked))
                await todos.ArchiveAsync(row.OrganizationId, new(row.InitiatorEmployeeId, row.InitiatorInstallationId),
                    new(card.Id, card.Revision, "issue-transfer-card:" + row.Id.ToString("N") + ":" + card.Id.ToString("N")), ct);
        }
        return complete;
    }
}
