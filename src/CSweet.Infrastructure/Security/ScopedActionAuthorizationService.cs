using CSweet.Application.Security;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

public sealed class ScopedActionAuthorizationService(CSweetDbContext db)
    : IScopedActionAuthorizationService
{
    public async Task<ScopedAuthorizationDecision> AuthorizeAsync(
        Guid organizationId,
        GrantSubjectKind subjectKind,
        Guid subjectId,
        string action,
        GrantScopeKind resourceScopeKind,
        Guid? resourceScopeId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var grant = await db.ScopedActionGrants.AsNoTracking()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == subjectKind &&
                x.SubjectId == subjectId &&
                x.Action == action &&
                x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now) &&
                (x.ScopeKind == GrantScopeKind.Organization ||
                 (x.ScopeKind == resourceScopeKind && x.ScopeId == resourceScopeId)))
            .OrderByDescending(x => x.ScopeKind == resourceScopeKind && x.ScopeId == resourceScopeId)
            .ThenByDescending(x => x.Revision)
            .Select(x => new { x.Id, x.Revision })
            .FirstOrDefaultAsync(cancellationToken);

        return grant is null
            ? new ScopedAuthorizationDecision(false, action)
            : new ScopedAuthorizationDecision(true, action, grant.Id, grant.Revision);
    }
}

