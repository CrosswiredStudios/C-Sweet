using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Contracts.Realtime;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Source-bound responsibility and commission registry. It never grants execution or product acceptance.</summary>
public sealed partial class WorkIssueService(CSweetDbContext db, IScopedActionAuthorizationService authorization, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Wire.WorkIssueDirectory> ReadAsync(Guid organization, Guid actor, Wire.ReadWorkIssuesRequest request, CancellationToken ct)
    {
        Wire.WorkIssueValidation.Validate(request);
        var board = await RequireBoardAsync(organization, actor, request.BoardId, false, ct);
        if (request.Page < 1 || request.PageSize is < 1 or > 100) throw new ArgumentException("Invalid issue discovery page.");
        var all = db.WorkIssueCases.AsNoTracking().Where(x => x.OrganizationId == organization && x.BoardId == request.BoardId);
        if (!request.IncludeHistory) all = all.Where(x => x.Status == "Open");
        var rows = await all.OrderBy(x => x.Id).Skip((request.Page - 1) * request.PageSize).Take(request.PageSize + 1).ToListAsync(ct);
        var ids = rows.Take(request.PageSize).Select(x => x.Id).ToHashSet();
        var commissions = await db.WorkIssueCommissions.AsNoTracking().Where(x => x.OrganizationId == organization && x.BoardId == request.BoardId)
            .OrderBy(x => x.Id).Skip((request.Page - 1) * request.PageSize).Take(request.PageSize + 1).ToListAsync(ct);
        var visible = new List<WorkIssueCase>();
        foreach (var row in rows.Take(request.PageSize)) if (await CanReadSourceAsync(organization, actor, Read<Wire.WorkApprovedSource>(row.SourceJson), ct)) visible.Add(row);
        var visibleIds = visible.Select(x => x.Id).ToHashSet();
        var visibleCommissions = new List<Wire.WorkIssueCommissionResponse>();
        foreach (var c in commissions.Take(request.PageSize))
            if (await CanReadSourceAsync(organization, actor, Read<Wire.WorkApprovedSource>(c.SourceJson), ct)) visibleCommissions.Add(Map(c) with {
                OriginSessionIds = actor == c.InitiatorEmployeeId && c.Status is "Proposed" or "Accepted" or "Rejected"
                    ? Read<Wire.ReconcileWorkIssuesRequest>(c.Question).Issues.SelectMany(i => i.Links).Where(l => l.Kind == "Coordination").Select(l => l.Id).Distinct().ToArray() : null });
        return new(visible.Select(Map).ToArray(), visibleCommissions, rows.Count > request.PageSize || commissions.Count > request.PageSize) {
            CanReconcile = await CanReconcileAsync(organization, actor, board.Id, ct), AccountableEmployeeId = board.ManagerOrganizationUserId };
    }

    public async Task<Wire.WorkIssueDirectory> ReconcileAsync(Guid organization, Guid actor, Wire.ReconcileWorkIssuesRequest request, CancellationToken ct)
    {
        Wire.WorkIssueValidation.Validate(request);
        var board = await RequireBoardAsync(organization, actor, request.BoardId, true, ct);
        if (!await CanReadSourceAsync(organization, actor, request.Source, ct)) throw new UnauthorizedAccessException("The accepted source is not readable by this manager.");
        await RequireSourceAsync(organization, board, request.Source, ct);
        ValidateKey(request.IdempotencyKey);
        var digest = MutationContent.Digest(new { request.BoardId, request.Source, request.Issues });
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "WorkBoards" WHERE "Id" = {board.Id} FOR UPDATE""", ct);
        if (await ReplayAsync<Wire.WorkIssueDirectory>(organization, actor, "issue-reconcile:" + board.Id.ToString("N"), request.IdempotencyKey, digest, ct) is { } replay) return replay;
        var result = new List<WorkIssueCase>();
        var changed = false; var presentationChanged = false;
        foreach (var update in request.Issues)
        {
            ValidateKey(update.ObligationKey);
            if (string.IsNullOrWhiteSpace(update.Statement) || string.IsNullOrWhiteSpace(update.NextAction) ||
                update.Status is not ("Open" or "Resolved" or "Superseded") || update.Phase is not ("Working" or "AwaitingEvidence" or "AwaitingDecision" or "AwaitingReview"))
                throw new ArgumentException("An issue needs an approved obligation, accountable next action and valid lifecycle.");
            if (update.Status != "Open" && string.IsNullOrWhiteSpace(update.ResolutionEvidence)) throw new ArgumentException("Resolution or supersession requires explicit evidence.");
            var row = await db.WorkIssueCases.Where(x => x.OrganizationId == organization && x.BoardId == board.Id && x.ObligationKey == update.ObligationKey)
                .OrderByDescending(x => x.Episode).FirstOrDefaultAsync(ct);
            if (update.IssueId.HasValue && update.IssueId != row?.Id) throw new InvalidOperationException("The issue identity or episode changed.");
            if (row is not null && update.ExpectedRevision != row.Revision) throw new DbUpdateConcurrencyException("Read the current issue revision before changing it.");
            if (row is not null && row.Status != "Open" && update.Status == "Open")
            {
                if (Read<Wire.WorkApprovedSource>(row.SourceJson) == request.Source)
                {
                    if (update.ReopeningEvidence is not { } fresh || Read<Wire.WorkIssueLink[]>(row.LinksJson).Any(l => l.Kind == fresh.Kind && l.Id == fresh.Id))
                        throw new InvalidOperationException("A resolved unchanged obligation needs new recorded evidence; rewording cannot reopen it.");
                    await RequireLinkAsync(organization, board, actor, fresh, ct);
                }
                var previous = row;
                row = new() { Id = Guid.NewGuid(), OrganizationId = organization, BoardId = board.Id, WorkstreamId = board.WorkstreamId!.Value,
                    ObligationKey = update.ObligationKey, Episode = previous.Episode + 1, PreviousIssueId = previous.Id };
                db.WorkIssueCases.Add(row);
            }
            if (row is null)
            {
                if (update.ExpectedRevision.HasValue) throw new DbUpdateConcurrencyException("The issue does not exist at that revision.");
                row = new() { Id = Guid.NewGuid(), OrganizationId = organization, BoardId = board.Id, WorkstreamId = board.WorkstreamId!.Value, ObligationKey = update.ObligationKey };
                db.WorkIssueCases.Add(row);
            }
            var presentationBefore = MutationContent.Digest(new { row.Statement, row.Detail, row.BriefTitle, row.BriefSummary });
            var before = MutationContent.Digest(new { row.Status, row.Phase, row.AccountableEmployeeId, row.NextAction, row.SourceJson, row.ScopeKeysJson, row.LinksJson, row.ResolutionEvidence });
            var links = update.Links.Concat(update.ReopeningEvidence is null ? [] : new[] { update.ReopeningEvidence }).Distinct().ToArray();
            foreach (var link in links) await RequireLinkAsync(organization, board, actor, link, ct);
            if (update.Status != "Open")
            {
                var linkedDecisions = Read<Wire.WorkIssueLink[]>(row.LinksJson).Concat(links).Where(l => l.Kind == "Decision").Select(l => l.Id).ToArray();
                foreach (var linked in linkedDecisions)
                {
                    var id = linked; var seen = new HashSet<Guid>(); var satisfied = false;
                    while (seen.Add(id))
                    {
                        var decision = await db.WorkstreamDecisions.SingleOrDefaultAsync(d => d.OrganizationId == organization && d.WorkstreamId == board.WorkstreamId && d.Id == id, ct);
                        if (decision is { Status: "Superseded", SupersededByDecisionId: Guid successor }) { id = successor; continue; }
                        satisfied = decision is { Status: "Decided" or "Cancelled" }; break;
                    }
                    if (!satisfied) throw new InvalidOperationException("A current required decision must be resolved or authoritatively superseded before retiring its issue.");
                }
            }
            row.Statement = update.Statement.Trim(); row.Detail = update.Detail; row.Status = update.Status; row.Phase = update.Phase;
            row.BriefTitle = update.BriefTitle ?? row.BriefTitle;
            row.BriefSummary = update.BriefSummary ?? row.BriefSummary;
            row.AccountableEmployeeId = actor; row.NextAction = update.NextAction.Trim(); row.SourceJson = Serialize(request.Source);
            row.ScopeKeysJson = Serialize(update.ScopeKeys.Distinct().Order(StringComparer.Ordinal).ToArray());
            row.LinksJson = Serialize(Read<Wire.WorkIssueLink[]>(row.LinksJson).Concat(links).Distinct().ToArray());
            row.ResolutionEvidence = update.ResolutionEvidence; row.Revision++; row.UpdatedAt = clock.GetUtcNow();
            presentationChanged |= presentationBefore != MutationContent.Digest(new { row.Statement, row.Detail, row.BriefTitle, row.BriefSummary });
            changed |= before != MutationContent.Digest(new { row.Status, row.Phase, row.AccountableEmployeeId, row.NextAction, row.SourceJson, row.ScopeKeysJson, row.LinksJson, row.ResolutionEvidence });
            if (row.Status != "Open") await QueueRetirementAsync(organization, actor, row, ct);
            result.Add(row);
        }
        var response = new Wire.WorkIssueDirectory(result.Select(Map).ToArray(), []);
        AddReceipt(organization, actor, "issue-reconcile:" + board.Id.ToString("N"), request.IdempotencyKey, digest, response);
        if (changed || presentationChanged) await QueueChangedAsync(organization, board, actor, ct, wakeOwner: changed);
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return response;
    }

    public async Task<Wire.WorkIssueCommissionResponse> CommissionAsync(Guid organization, Guid actor, Guid installation, Wire.CommissionWorkIssuesRequest request, CancellationToken ct)
    {
        Wire.WorkIssueValidation.Validate(request);
        var board = await RequireBoardAsync(organization, actor, request.BoardId, true, ct);
        if (!await CanReadSourceAsync(organization, actor, request.Source, ct)) throw new UnauthorizedAccessException("The commission source is not readable by this manager.");
        await RequireSourceAsync(organization, board, request.Source, ct); ValidateKey(request.IdempotencyKey);
        if (request.IssueIds.Count == 0 || request.IssueIds.Distinct().Count() != request.IssueIds.Count || string.IsNullOrWhiteSpace(request.Question)) throw new ArgumentException("Select distinct current issues and a scoped question.");
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == organization && x.Id == actor && x.AgentInstallationId == installation && x.IsActive, ct)) throw new UnauthorizedAccessException();
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == organization && x.Id == request.TargetEmployeeId && x.IsActive && x.AgentInstallationId != null, ct)) throw new UnauthorizedAccessException("The specialist is unavailable.");
        var digest = MutationContent.Digest(new { request.BoardId, request.TargetEmployeeId, request.IssueIds, request.Source, request.Question, request.SupersededSessionIds });
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "WorkBoards" WHERE "Id" = {board.Id} FOR UPDATE""", ct);
        if (await ReplayAsync<Wire.WorkIssueCommissionResponse>(organization, actor, "issue-commission:" + board.Id.ToString("N"), request.IdempotencyKey, digest, ct) is { } replay) return replay;
        var members = await db.WorkIssueCases.Where(x => x.OrganizationId == organization && x.BoardId == board.Id && request.IssueIds.Contains(x.Id)).ToListAsync(ct);
        if (members.Count != request.IssueIds.Count || members.Any(x => x.Status != "Open" || Read<Wire.WorkApprovedSource>(x.SourceJson) != request.Source)) throw new InvalidOperationException("Commission only open issues on the exact accepted basis.");
        foreach (var sessionId in request.SupersededSessionIds.Distinct())
        {
            var session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.OrganizationId == organization &&
                x.WorkstreamId == board.WorkstreamId && x.InitiatorOrganizationUserId == actor && x.TargetOrganizationUserId == request.TargetEmployeeId, ct);
            if (session is null) throw new UnauthorizedAccessException("A carried-forward consultation must belong to this manager, project and specialist.");
            // The dispatcher additionally checks the original typed request and its exact source before retirement.
        }
        var prior = await db.WorkIssueCommissions.Where(x => x.OrganizationId == organization && x.BoardId == board.Id && x.TargetEmployeeId == request.TargetEmployeeId && (x.Status == "Pending" || x.Status == "Dispatched")).ToListAsync(ct);
        if (prior.Any(x => Read<Wire.WorkApprovedSource>(x.SourceJson) == request.Source && Read<Guid[]>(x.IssueIdsJson).Intersect(request.IssueIds).Any()))
            throw new InvalidOperationException("A current commission already owns this advice request; reuse it or commission only distinct remaining issues.");
        var row = new WorkIssueCommission { Id = Guid.NewGuid(), OrganizationId = organization, BoardId = board.Id,
            InitiatorEmployeeId = actor, InitiatorInstallationId = installation, TargetEmployeeId = request.TargetEmployeeId,
            IssueIdsJson = Serialize(request.IssueIds), SourceJson = Serialize(request.Source), Question = request.Question, SupersededSessionIdsJson = Serialize(request.SupersededSessionIds.Distinct().ToArray()), NextAttemptAt = clock.GetUtcNow() };
        db.WorkIssueCommissions.Add(row);
        var response = Map(row); AddReceipt(organization, actor, "issue-commission:" + board.Id.ToString("N"), request.IdempotencyKey, digest, response);
        await QueueChangedAsync(organization, board, actor, ct);
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return response;
    }

    private async Task QueueRetirementAsync(Guid organization, Guid actor, WorkIssueCase issue, CancellationToken ct)
    {
        var sessions = Read<Wire.WorkIssueLink[]>(issue.LinksJson).Where(l => l.Kind == "Coordination").Select(l => l.Id).Distinct().ToArray();
        if (sessions.Length == 0) return;
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == organization && x.Id == actor && x.IsActive, ct);
        if (employee.AgentInstallationId is not Guid installation) return;
        var issueIds = Serialize(new[] { issue.Id });
        var retained = await db.WorkIssueCommissions.Where(c => c.OrganizationId == organization && c.BoardId == issue.BoardId &&
            c.IssueIdsJson == issueIds && (c.Status == "RetirementPending" || c.Status == "Retired")).ToListAsync(ct);
        if (retained.Any(c => Read<Guid[]>(c.SupersededSessionIdsJson).Order().SequenceEqual(sessions.Order()))) return;
        db.WorkIssueCommissions.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, BoardId = issue.BoardId,
            InitiatorEmployeeId = actor, InitiatorInstallationId = installation, TargetEmployeeId = actor,
            IssueIdsJson = issueIds, SourceJson = issue.SourceJson, Question = "Retire source-bound obsolete tracking after verified responsibility reconciliation",
            SupersededSessionIdsJson = Serialize(sessions), Status = "RetirementPending", NextAttemptAt = clock.GetUtcNow() });
    }

    internal async Task<WorkBoard> RequireBoardAsync(Guid organization, Guid actor, Guid boardId, bool edit, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == organization && x.Id == actor && x.IsActive, ct)) throw new UnauthorizedAccessException();
        var board = await db.WorkBoards.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == boardId && x.ArchivedAt == null, ct) ?? throw new UnauthorizedAccessException();
        var grant = await WorkActorAuthorization.AuthorizeAsync(db, authorization, organization, actor,
            edit ? CSweet.Contracts.WorkManagement.WorkItemActions.RevisePlanning : CSweet.Contracts.WorkManagement.WorkBoardActions.Read, GrantScopeKind.Board, boardId, ct);
        var current = board.ManagerOrganizationUserId;
        var seen = new HashSet<Guid>();
        while (current.HasValue && current != actor && seen.Add(current.Value))
            current = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == organization && x.Id == current && x.IsActive).Select(x => x.ReportsToOrganizationUserId).SingleOrDefaultAsync(ct);
        if (!grant.Allowed || edit && current != actor || board.WorkstreamId is null) throw new UnauthorizedAccessException("Issue work requires current scoped board authority.");
        return board;
    }

    internal async Task<bool> CanReadSourceAsync(Guid organization, Guid actor, Wire.WorkApprovedSource source, CancellationToken ct)
    {
        var granted = await WorkActorAuthorization.AuthorizeAsync(db, authorization, organization, actor,
            CSweet.Contracts.Core.ArtifactActions.Read, GrantScopeKind.Artifact, source.DocumentId, ct);
        return granted.Allowed;
    }

    internal async Task RequireSourceAsync(Guid organization, WorkBoard board, Wire.WorkApprovedSource source, CancellationToken ct)
    {
        if (!await db.CoreArtifacts.AnyAsync(x => x.OrganizationId == organization && x.Id == source.DocumentId && x.WorkstreamId == board.WorkstreamId && x.AcceptedRevisionId == source.RevisionId && x.ArchivedAt == null, ct) ||
            !await db.ArtifactRevisions.AnyAsync(x => x.OrganizationId == organization && x.ArtifactId == source.DocumentId && x.Id == source.RevisionId && x.ContentSha256 == source.Digest && x.Status == ArtifactRevisionStatus.Accepted, ct))
            throw new InvalidOperationException("The issue basis must be the exact current accepted project document.");
    }

    private async Task RequireLinkAsync(Guid organization, WorkBoard board, Guid actor, Wire.WorkIssueLink link, CancellationToken ct)
    {
        var allowed = link.Kind switch {
            "Coordination" => await db.AgentCoordinationSessions.AnyAsync(x => x.OrganizationId == organization && x.Id == link.Id && x.WorkstreamId == board.WorkstreamId && (x.InitiatorOrganizationUserId == actor || x.TargetOrganizationUserId == actor), ct),
            "WorkItem" => await db.CoreWorkTasks.AnyAsync(x => x.OrganizationId == organization && x.Id == link.Id && x.BoardId == board.Id, ct),
            "Decision" => await db.WorkstreamDecisions.AnyAsync(x => x.OrganizationId == organization && x.Id == link.Id && x.WorkstreamId == board.WorkstreamId, ct),
            "Incident" => await db.ProjectIncidents.AnyAsync(x => x.OrganizationId == organization && x.Id == link.Id && x.WorkstreamId == board.WorkstreamId, ct),
            _ => false
        };
        if (!allowed) throw new UnauthorizedAccessException("The issue link does not bind this manager's authorized work.");
    }

    internal async Task QueueChangedAsync(Guid organization, WorkBoard board, Guid actor, CancellationToken ct, bool wakeOwner = true)
    {
        var now = clock.GetUtcNow();
        var people = await db.ScopedActionGrants.Where(x => x.OrganizationId == organization && x.SubjectKind == GrantSubjectKind.OrganizationUser && x.Action == CSweet.Contracts.WorkManagement.WorkBoardActions.Read &&
            x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now) && (x.ScopeKind == GrantScopeKind.Organization || x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id)).Select(x => x.SubjectId).Distinct().ToArrayAsync(ct);
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, RecipientOrganizationUserIdsJson = Serialize(people),
            EventType = AppRealtimeEvents.WorkBoardChanged, Subject = $"organizations/{organization:D}/work/boards/{board.Id:D}", DataJson = Serialize(new { boardId = board.Id, changeType = "issues.changed" }),
            Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == organization && x.Id == actor, ct);
        if (wakeOwner && employee.AgentInstallationId is Guid installation)
        {
            var id = Guid.NewGuid();
            db.AgentPlatformEventOutbox.Add(new() { Id = id, OrganizationId = organization, TargetInstallationId = installation, EventType = Wire.WorkIssueEvents.Changed,
                DataJson = Serialize(new { boardId = board.Id, workstreamId = board.WorkstreamId }), IdempotencyKey = "issues-changed:" + id.ToString("N"),
                Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
        }
    }

    private async Task<T?> ReplayAsync<T>(Guid org, Guid actor, string resource, string key, string digest, CancellationToken ct) where T : class
    {
        var saved = await db.DurableMutationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.PrincipalId == actor && x.ResourceKey == resource && x.MutationKey == key, ct);
        if (saved is null) return null;
        if (saved.ContentDigest != digest) throw new InvalidOperationException("idempotency.content_mismatch: This mutation already binds different content.");
        return Read<T>(saved.ResultJson);
    }
    private void AddReceipt<T>(Guid org, Guid actor, string resource, string key, string digest, T result) => db.DurableMutationReceipts.Add(new() {
        Id = Guid.NewGuid(), OrganizationId = org, PrincipalId = actor, ResourceKey = resource, MutationKey = key, ContentDigest = digest, ResultJson = Serialize(result), CreatedAt = clock.GetUtcNow() });
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    private static void ValidateKey(string key) { if (string.IsNullOrWhiteSpace(key) || key.Length > 160) throw new ArgumentException("Provide a bounded opaque mutation or obligation identity."); }
    internal static Wire.WorkIssueCaseResponse Map(WorkIssueCase x) => new(x.Id, x.BoardId, x.WorkstreamId, x.ObligationKey, x.Statement, x.Detail, x.Status, x.Phase,
        x.AccountableEmployeeId, x.NextAction, Read<Wire.WorkApprovedSource>(x.SourceJson), Read<string[]>(x.ScopeKeysJson), Read<Wire.WorkIssueLink[]>(x.LinksJson), x.ResolutionEvidence, x.Revision, x.UpdatedAt) { Episode = x.Episode, PreviousIssueId = x.PreviousIssueId, BriefTitle = x.BriefTitle, BriefSummary = x.BriefSummary };
    internal static Wire.WorkIssueCommissionResponse Map(WorkIssueCommission x) => new(x.Id, x.BoardId, x.TargetEmployeeId, Read<Guid[]>(x.IssueIdsJson), Read<Wire.WorkApprovedSource>(x.SourceJson), x.Status, x.SessionId, x.Status is "Accepted" or "Rejected" ? null : x.Failure, x.Revision) {
        RequestedByEmployeeId = x.InitiatorEmployeeId, DecisionReason = x.Status is "Accepted" or "Rejected" ? x.Failure : null,
        ProposedChanges = x.Status is "Proposed" or "Accepted" or "Rejected" ? Read<Wire.ReconcileWorkIssuesRequest>(x.Question).Issues.Select(c => c with { Links = c.Links.Where(l => l.Kind != "Coordination").ToArray() }).ToArray() : null };
}
