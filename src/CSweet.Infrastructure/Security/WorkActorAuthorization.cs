using CSweet.Application.Security;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

/// <summary>Resolve the current employee to the principal that actually holds work authority.</summary>
internal static class WorkActorAuthorization
{
    internal static async Task<ScopedAuthorizationDecision> AuthorizeAsync(CSweetDbContext db,
        IScopedActionAuthorizationService authorization, Guid organization, Guid employee, string action,
        GrantScopeKind scope, Guid? scopeId, CancellationToken ct)
    {
        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organization && x.Id == employee && x.IsActive && x.ArchivedAt == null, ct);
        if (member is null) return new(false, action);
        if (scope == GrantScopeKind.Board)
            return await WorkResourceAuthorization.AuthorizeBoardAsync(db, authorization, organization,
                member.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
                member.AgentInstallationId ?? member.Id, action, scopeId, ct);
        return await authorization.AuthorizeAsync(organization,
            member.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
            member.AgentInstallationId ?? member.Id, action, scope, scopeId, ct);
    }
}
