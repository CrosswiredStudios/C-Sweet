using System.Data;
using CSweet.Domain.Core;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    internal const string DeclaredTicketMoveGuidance =
        "This ticket is moved by its delivery plan. To retry a blocked delivery check, move the ticket to a ready or working column, " +
        "or use Retry validation on the project's Releases page.";

    /// <summary>
    /// A person moving a declared delivery ticket on the board. Moving a blocked ticket to a ready or working column
    /// retries the check it stopped at, as it does for sprint cards (QFN Games, 2026-11-12: the move was accepted and
    /// silently reverted, so nothing re-ran). Any other manual move is refused with guidance, because the plan owns
    /// the ticket's column. Returns false when the item is not a declared delivery ticket of an active or paused plan.
    /// </summary>
    public async Task<bool> MoveDeclaredTicketAsync(Guid org, Guid actorId, Guid ticketId, bool retryRequested, CancellationToken ct = default)
    {
        var candidates = await db.WorkDeliveryPlans.AsNoTracking()
            .Where(x => x.OrganizationId == org && (x.Status == "Active" || x.Status == "Paused"))
            .Select(x => new { x.Id, x.ScopesJson }).ToListAsync(ct);
        var planId = candidates.FirstOrDefault(p => Decode<WorkDeliveryScopeSnapshot[]>(p.ScopesJson)
            .Any(s => s.TicketWorkflow?.TicketId == ticketId))?.Id;
        if (planId is null) return false;
        if (!retryRequested) throw new InvalidOperationException(DeclaredTicketMoveGuidance);

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, planId.Value, WorkDeliveryCapabilities.Control, ct);
        var actor = await MemberAsync(org, actorId, ct);
        if (actor.Id != plan.ManagerOrganizationUserId && actor.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only the release manager or an organization owner can retry a delivery ticket.");
        if (plan.Status != "Active") throw new InvalidOperationException("Resume the delivery plan before retrying its tickets.");
        var execution = plan.Executions.SingleOrDefault(e => e.ScopeRevision == plan.ScopeRevision &&
            ScopeFor(plan, e).TicketWorkflow?.TicketId == ticketId);
        if (execution is not { Status: "Blocked", AcceptanceJson: null })
            throw new InvalidOperationException("This delivery ticket has not stopped at a failed check, so there is nothing to retry. " + DeclaredTicketMoveGuidance);
        if (Corrections(plan).Any(c => c.ExecutionId == execution.Id && c.FollowUpPlanId.HasValue && c.Status != "Completed"))
            throw new InvalidOperationException("This rejected delivery has linked corrective work. Continue its follow-up plan; do not restart the superseded candidate writer.");
        await ValidateCurrentScopeAsync(plan, ct);
        await RetryStoppedValidationAsync(plan, execution, actor, "Retried by moving the ticket on the board.", ct);
        plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        await SynchronizeTicketAsync(plan, execution, ct);
        Queue(plan, "retry-validation");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }
}
