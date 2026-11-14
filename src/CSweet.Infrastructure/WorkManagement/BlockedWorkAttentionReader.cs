using CSweet.Application.WorkManagement;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Projects canonical blocked work into the same recovery queue for every role and business type.</summary>
public sealed class BlockedWorkAttentionReader(CSweetDbContext db, IPersonalTodoService todos, TimeProvider clock)
{
    public async Task<IReadOnlyList<BlockedWorkAttentionResponse>> ReadAsync(
        Guid organizationId, PersonalTodoActor actor, CancellationToken token = default)
    {
        // Reuse the authorized directory. Visibility alone does not authorize a retry.
        var directory = await todos.ListAsync(organizationId, actor, cancellationToken: token);
        var now = clock.GetUtcNow();
        var kind = actor.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
        var subject = actor.AgentInstallationId ?? actor.OrganizationUserId;
        var retryBoards = await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.SubjectKind == kind && x.SubjectId == subject &&
                x.ScopeKind == GrantScopeKind.Board && x.Action == PersonalTodoActions.Requeue &&
                x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now))
            .Select(x => x.ScopeId).ToListAsync(token);
        var blocked = directory.Boards.SelectMany(board => board.Items
                .Where(item => item.ArchivedAt is null && item.Status == Wire.PersonalTodoStatuses.Blocked)
                .Select(item => (Board: board, Item: item))).DistinctBy(x => x.Item.Id).ToArray();
        var blockedIds = blocked.Select(x => x.Item.Id).ToHashSet();
        return blocked.Where(x => x.Item.PlanRootId is not { } root || root == x.Item.Id || !blockedIds.Contains(root))
            .OrderBy(x => x.Item.UpdatedAt).Select(x => new BlockedWorkAttentionResponse(
                x.Item.Id, x.Board.OwnerOrganizationUserId, x.Board.OwnerDisplayName, x.Board.ManagerDisplayName,
                x.Item.Title, x.Item.BlockReason ?? "Review the affected work and its recorded execution before retrying.",
                x.Item.Revision, x.Item.IsExecutable && retryBoards.Contains(x.Item.BoardId) &&
                    (x.Item.PlanRootId is null || x.Item.PlanRootId == x.Item.Id),
                $"/organizations/{organizationId:D}/employees/{x.Board.OwnerOrganizationUserId:D}?tab=personal-board&item={x.Item.PlanRootId ?? x.Item.Id:D}",
                x.Item.WorkContext?.DecisionId)).ToArray();
    }
}
