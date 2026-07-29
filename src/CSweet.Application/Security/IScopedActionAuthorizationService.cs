using CSweet.Domain.Security;

namespace CSweet.Application.Security;

public sealed record ScopedAuthorizationDecision(
    bool Allowed,
    string Action,
    Guid? GrantId = null,
    long? GrantRevision = null);

public interface IScopedActionAuthorizationService
{
    Task<ScopedAuthorizationDecision> AuthorizeAsync(
        Guid organizationId,
        GrantSubjectKind subjectKind,
        Guid subjectId,
        string action,
        GrantScopeKind resourceScopeKind,
        Guid? resourceScopeId,
        CancellationToken cancellationToken = default);
}

