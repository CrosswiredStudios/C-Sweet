using CSweet.Application.Security;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;
using C = CSweet.Contracts.WorkManagement;
namespace CSweet.Infrastructure.WorkManagement;

public sealed record WorkRecoveryOperation(Guid OrganizationId, Guid OwnerEmployeeId, Guid? BoardId, string Capability,
    IReadOnlySet<string> GrantedCapabilities)
{
    public Guid WorkItemId { get; init; }
    public Guid? OwnerInstallationId { get; init; }
    public W.PersonalTodoWorkContext? WorkContext { get; init; }
}
public sealed record WorkRecoveryAuthorization(bool? Allowed, string Basis, string Explanation, string? ContinuationCapability = null);
/// <summary>Trusted platform extension for current operation facts. An agent cannot supply its own authorization verdict.</summary>
public interface IWorkRecoveryAuthorizationPolicy
{
    bool Handles(string capability);
    Task<WorkRecoveryAuthorization> EvaluateAsync(WorkRecoveryOperation operation, CancellationToken ct);
}

/// <summary>Planning/read operations use their normal board policy. Execution, money and approval decisions are not inferred.</summary>
public sealed class BoardPlanningRecoveryPolicy(CSweetDbContext db, IScopedActionAuthorizationService authorization, WorkIssueService issues)
    : IWorkRecoveryAuthorizationPolicy
{
    private static readonly IReadOnlySet<string> Actions = new HashSet<string>([
        C.WorkBoardActions.Read, C.WorkBoardActions.Configure, C.WorkBoardActions.ConfigureColumns,
        C.WorkItemActions.Read, C.WorkItemActions.ReadTypes, C.WorkItemActions.Create, C.WorkItemActions.RevisePlanning, C.WorkItemActions.Estimate,
        C.WorkSprintActions.Read, C.WorkSprintActions.Create, C.WorkSprintActions.ManageScope, C.WorkSprintActions.ManageCapacity, C.WorkSprintActions.ReadReports,
        C.WorkOrchestrationActions.Read, C.WorkOrchestrationActions.Preflight, C.WorkOrchestrationActions.ConfigureSoftwareTemplate, C.WorkOrchestrationActions.ConfigureProfile], StringComparer.Ordinal);
    public static bool IsPlanningAction(string action) => Actions.Contains(action);
    public bool Handles(string capability) => Actions.Contains(capability) || capability is W.WorkIssueCapabilities.Read or W.WorkIssueCapabilities.Reconcile or W.WorkIssueCapabilities.Commission;
    public async Task<WorkRecoveryAuthorization> EvaluateAsync(WorkRecoveryOperation op, CancellationToken ct)
    {
        if (op.BoardId is not Guid board) return new(null, "unbound-board", "The recorded operation has no unambiguous current board binding.");
        var issue = op.Capability is W.WorkIssueCapabilities.Read or W.WorkIssueCapabilities.Reconcile or W.WorkIssueCapabilities.Commission;
        var action = issue ? op.Capability == W.WorkIssueCapabilities.Read ? C.WorkBoardActions.Read : C.WorkItemActions.RevisePlanning : op.Capability;
        var current = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == board && b.OrganizationId == op.OrganizationId && b.ArchivedAt == null, ct);
        var grant = await WorkActorAuthorization.AuthorizeAsync(db, authorization, op.OrganizationId, op.OwnerEmployeeId, action, GrantScopeKind.Board, board, ct);
        var allowed = current is not null && grant.Allowed;
        if (issue) {
            try { await issues.RequireBoardAsync(op.OrganizationId, op.OwnerEmployeeId, board, op.Capability != W.WorkIssueCapabilities.Read, ct); }
            catch (UnauthorizedAccessException) { allowed = false; }
        }
        if (op.Capability is C.WorkOrchestrationActions.Preflight or C.WorkOrchestrationActions.ConfigureSoftwareTemplate or C.WorkOrchestrationActions.ConfigureProfile)
            allowed &= current?.ManagerOrganizationUserId == op.OwnerEmployeeId;
        string? continuation = null;
        if (!allowed && op.Capability == W.WorkIssueCapabilities.Reconcile && op.GrantedCapabilities.Contains(W.WorkIssueCapabilities.Propose)) {
            try { await issues.RequireBoardAsync(op.OrganizationId, op.OwnerEmployeeId, board, false, ct); continuation = W.WorkIssueCapabilities.Propose; allowed = true; }
            catch (UnauthorizedAccessException) { }
        }
        var basis = MutationContent.Digest(new { policy = "board-planning-recovery.v1", current?.Id, current?.ManagerOrganizationUserId, current?.TeamId,
            action, scopedGrantAllowed = grant.Allowed, grant.GrantId, grant.GrantRevision, allowed, continuation });
        var reason = continuation is not null ? "The original write remains denied. This installation can submit a source-bound contribution to the authorized owner. Resume that intent once through the supported path; no editing authority is granted."
            : allowed ? "Current scoped authority is available for the recorded planning operation. Resume the original intent once; normal ownership, source, approval and execution checks still apply."
            : "The owner's operation is still denied. Resolve its actual scoped authority or adjust the owned intent before retrying.";
        return new(allowed, basis, reason, continuation);
    }
}
