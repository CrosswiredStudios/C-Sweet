using CSweet.Application.Security;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

/// <summary>Effective board authority. Team scope follows the board's recorded relationship, never a caller-supplied team.</summary>
public static class WorkResourceAuthorization
{
    public static async Task<ScopedAuthorizationDecision> AuthorizeBoardAsync(CSweetDbContext db,
        IScopedActionAuthorizationService authorization, Guid organization, GrantSubjectKind kind, Guid subject,
        string action, Guid? boardId, CancellationToken ct)
    {
        if (boardId is Guid board && !await db.WorkBoards.AsNoTracking().AnyAsync(b => b.Id == board && b.OrganizationId == organization, ct))
            return new(false, action);
        var decision = await authorization.AuthorizeAsync(organization, kind, subject, action,
            boardId.HasValue ? GrantScopeKind.Board : GrantScopeKind.Organization, boardId, ct);
        if (decision.Allowed || !boardId.HasValue) return decision;
        var team = await db.WorkBoards.AsNoTracking().Where(b => b.OrganizationId == organization && b.Id == boardId)
            .Select(b => b.TeamId).SingleOrDefaultAsync(ct);
        return team.HasValue ? await authorization.AuthorizeAsync(organization, kind, subject, action, GrantScopeKind.Team, team, ct) : decision;
    }
}
