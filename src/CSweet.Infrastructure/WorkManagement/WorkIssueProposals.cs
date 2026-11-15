using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkIssueService
{
    private async Task<bool> CanReconcileAsync(Guid org, Guid actor, Guid board, CancellationToken ct)
    {
        try { await RequireBoardAsync(org, actor, board, true, ct); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>A readable source can support a proposal, not a permission to change the plan or its issue verdicts.</summary>
    public async Task<Wire.WorkIssueCommissionResponse> ProposeAsync(Guid org, Guid actor, Guid installation,
        Wire.ReconcileWorkIssuesRequest request, CancellationToken ct)
    {
        Wire.WorkIssueValidation.Validate(request);
        var board = await RequireBoardAsync(org, actor, request.BoardId, false, ct);
        if (!await CanReadSourceAsync(org, actor, request.Source, ct) || board.ManagerOrganizationUserId is not Guid manager)
            throw new UnauthorizedAccessException("A proposal needs readable approved sources and a current accountable owner.");
        await RequireSourceAsync(org, board, request.Source, ct);
        if (!await db.CoreOrganizationUsers.AnyAsync(u => u.Id == actor && u.OrganizationId == org && u.IsActive && u.ArchivedAt == null && u.AgentInstallationId == installation, ct))
            throw new UnauthorizedAccessException();
        if (request.Issues.Any(c => c.ReopeningEvidence?.Kind == "Coordination"))
            throw new ArgumentException("Reopening requires evidence readable by the accountable owner, such as a recorded work item, incident or decision.");
        foreach (var change in request.Issues)
            foreach (var link in change.Links.Concat(change.ReopeningEvidence is null ? [] : new[] { change.ReopeningEvidence }))
                await RequireLinkAsync(org, board, actor, link, ct);
        var digest = MutationContent.Digest(request);
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "WorkBoards" WHERE "Id" = {board.Id} FOR UPDATE""", ct);
        if (await ReplayAsync<Wire.WorkIssueCommissionResponse>(org, actor, "issue-proposal:" + board.Id.ToString("N"), request.IdempotencyKey, digest, ct) is { } replay) return replay;
        var row = new WorkIssueCommission { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id,
            InitiatorEmployeeId = actor, InitiatorInstallationId = installation, TargetEmployeeId = manager,
            SourceJson = Serialize(request.Source), IssueIdsJson = "[]", Question = Serialize(request), Status = "Proposed", NextAttemptAt = clock.GetUtcNow() };
        db.WorkIssueCommissions.Add(row);
        var result = Map(row);
        AddReceipt(org, actor, "issue-proposal:" + board.Id.ToString("N"), request.IdempotencyKey, digest, result);
        await QueueChangedAsync(org, board, manager, ct);
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }

    public async Task<Wire.WorkIssueCommissionResponse> DecideProposalAsync(Guid org, Guid actor,
        Wire.DecideWorkIssueProposalRequest request, CancellationToken ct)
    {
        Wire.WorkIssueValidation.Validate(request);
        var row = await db.WorkIssueCommissions.SingleAsync(c => c.Id == request.ProposalId && c.OrganizationId == org, ct);
        var board = await RequireBoardAsync(org, actor, row.BoardId, true, ct);
        if (board.ManagerOrganizationUserId != actor) throw new UnauthorizedAccessException("Only the current work owner decides a contribution.");
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) { await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "WorkIssueCommissions" WHERE "Id" = {row.Id} FOR UPDATE""", ct); await db.Entry(row).ReloadAsync(ct); }
        var digest = MutationContent.Digest(request);
        if (await ReplayAsync<Wire.WorkIssueCommissionResponse>(org, actor, "issue-proposal-decision", request.IdempotencyKey, digest, ct) is { } replay) return replay;
        if (row.Status != "Proposed" || row.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Review the current contribution revision.");
        var proposal = Read<Wire.ReconcileWorkIssuesRequest>(row.Question);
        if (request.Accept) await RequireSourceAsync(org, board, proposal.Source, ct);
        if (!await CanReadSourceAsync(org, actor, proposal.Source, ct)) throw new UnauthorizedAccessException("The owner must inspect the proposed source.");
        if (request.Accept)
        {
            // The contributor shared its source-bound finding, not its private conversation. Keep that provenance
            // in the existing commission receipt rather than copying private coordination links into the owner's case.
            if (request.ReviewedIssues is { } reviewed && !reviewed.Select(r => r.ObligationKey).ToHashSet().SetEquals(proposal.Issues.Select(i => i.ObligationKey)))
                throw new ArgumentException("The owner's reviewed bindings must cover exactly this contribution.");
            var changes = proposal.Issues.Select(c => {
                var current = request.ReviewedIssues?.Single(r => r.ObligationKey == c.ObligationKey);
                return c with { Links = c.Links.Where(l => l.Kind != "Coordination").ToArray(),
                    IssueId = current is null ? c.IssueId : current.IssueId, ExpectedRevision = current is null ? c.ExpectedRevision : current.Revision };
            }).ToArray();
            var saved = await ReconcileAsync(org, actor, proposal with { Issues = changes,
                IdempotencyKey = "contribution:" + row.Id.ToString("N") }, ct);
            row.IssueIdsJson = Serialize(saved.Issues.Select(i => i.Id).ToArray());
        }
        row.Status = request.Accept ? "Accepted" : "Rejected"; row.Failure = request.Reason;
        row.TargetEmployeeId = actor; row.Revision++;
        var result = Map(row);
        AddReceipt(org, actor, "issue-proposal-decision", request.IdempotencyKey, digest, result);
        await QueueChangedAsync(org, board, row.InitiatorEmployeeId, ct);
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }
}
