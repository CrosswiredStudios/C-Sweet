using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkBoardGrantService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit) : IWorkBoardGrantService
{
    private static readonly HashSet<string> KnownActions =
        WorkBoardActions.All.Concat(WorkItemActions.All).Concat(WorkSprintActions.All)
            .Concat(WorkAutomationActions.All)
            .ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<WorkBoardGrantResponse>> ListOrganizationAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireOrganizationManagerAsync(
            organizationId, applicationUserId, cancellationToken);
        return await ListScopeAsync(
            organizationId, GrantScopeKind.Organization, null, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkBoardGrantResponse>> SetOrganizationSubjectGrantsAsync(
        Guid organizationId,
        Guid applicationUserId,
        SetWorkBoardSubjectGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireOrganizationManagerAsync(
            organizationId, applicationUserId, cancellationToken);
        return await SetScopeGrantsAsync(
            organizationId, null, member, GrantScopeKind.Organization, request, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkBoardGrantResponse>> ListAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireManagerAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var grants = await db.ScopedActionGrants.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.ScopeKind == GrantScopeKind.Board &&
                        x.ScopeId == boardId &&
                        x.RevokedAt == null &&
                        (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .OrderBy(x => x.SubjectKind)
            .ThenBy(x => x.SubjectId)
            .ThenBy(x => x.Action)
            .Select(x => new WorkBoardGrantResponse(
                x.Id, x.SubjectKind.ToString(), x.SubjectId, x.Action,
                x.CanDelegate, x.Revision, x.GrantedAt, x.ExpiresAt))
            .ToListAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, member, "work.board.grants.read",
            new { count = grants.Count }, cancellationToken);
        return grants;
    }

    public async Task<IReadOnlyList<WorkBoardGrantResponse>> SetSubjectGrantsAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        SetWorkBoardSubjectGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireManagerAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        return await SetScopeGrantsAsync(
            organizationId, boardId, member, GrantScopeKind.Board, request, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkBoardGrantResponse>> SetScopeGrantsAsync(
        Guid organizationId,
        Guid? boardId,
        OrganizationUser member,
        GrantScopeKind scopeKind,
        SetWorkBoardSubjectGrantsRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<GrantSubjectKind>(request.SubjectKind, true, out var subjectKind) ||
            !Enum.IsDefined(subjectKind))
            throw new ArgumentException("The grant subject kind is invalid.");
        if (request.ExpiresAt.HasValue && request.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("Grant expiration must be in the future.");
        var actions = request.Actions.Distinct(StringComparer.Ordinal).ToList();
        if (actions.Any(action => !KnownActions.Contains(action)))
            throw new ArgumentException("One or more requested work actions are unknown.");
        await ValidateSubjectAsync(organizationId, subjectKind, request.SubjectId, cancellationToken);

        var issuerGrants = await ActiveIssuerGrantsAsync(
            organizationId, boardId, member.Id, cancellationToken);
        if (!issuerGrants.Any(x =>
                x.Action == WorkBoardActions.ManageGrants && x.CanDelegate))
            throw new UnauthorizedAccessException(
                "A delegable board grant-management grant is required to change access.");
        foreach (var action in actions)
        {
            var parent = issuerGrants.FirstOrDefault(x => x.Action == action && x.CanDelegate);
            if (parent is null)
                throw new UnauthorizedAccessException(
                    $"The current user cannot delegate '{action}' on this board.");
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await db.ScopedActionGrants.Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == subjectKind &&
            x.SubjectId == request.SubjectId &&
            x.ScopeKind == scopeKind &&
            x.ScopeId == boardId &&
            x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var grant in existing)
        {
            grant.RevokedAt = now;
            grant.Revision++;
        }

        foreach (var action in actions)
        {
            var parent = issuerGrants.First(x => x.Action == action && x.CanDelegate);
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                SubjectKind = subjectKind,
                SubjectId = request.SubjectId,
                Action = action,
                ScopeKind = scopeKind,
                ScopeId = boardId,
                CanDelegate = request.CanDelegate && parent.CanDelegate,
                ParentGrantId = parent.Id,
                GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                GrantedBySubjectId = member.Id,
                GrantedAt = now,
                ExpiresAt = request.ExpiresAt
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, boardId, member, "work.board.grants.set",
            new
            {
                subjectKind = subjectKind.ToString(),
                request.SubjectId,
                actions,
                request.CanDelegate,
                request.ExpiresAt
            }, cancellationToken);
        return await ListSubjectAsync(
            organizationId, boardId, scopeKind, subjectKind, request.SubjectId, cancellationToken);
    }

    private async Task<OrganizationUser> RequireManagerAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var member = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.EmployeeType == EmployeeType.Human &&
            x.IsActive, cancellationToken)
            ?? throw new UnauthorizedAccessException("The current user is not an active organization member.");
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(
            db, organizationId, member, cancellationToken);
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.OrganizationUser,
            member.Id,
            WorkBoardActions.ManageGrants,
            GrantScopeKind.Board,
            boardId,
            cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException("A delegable board grant-management grant is required.");
        if (!await db.WorkBoards.AnyAsync(x =>
                x.Id == boardId && x.OrganizationId == organizationId, cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        return member;
    }

    private async Task<List<ScopedActionGrant>> ActiveIssuerGrantsAsync(
        Guid organizationId,
        Guid? boardId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.OrganizationUser &&
            x.SubjectId == memberId &&
            x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now) &&
            (x.ScopeKind == GrantScopeKind.Organization ||
             (boardId.HasValue && x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId)))
            .ToListAsync(cancellationToken);
    }

    private async Task<OrganizationUser> RequireOrganizationManagerAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var member = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.EmployeeType == EmployeeType.Human &&
            x.IsActive, cancellationToken)
            ?? throw new UnauthorizedAccessException("The current user is not an active organization member.");
        await WorkBoardProvisioning.EnsureLegacyGrantsAsync(
            db, organizationId, member, cancellationToken);
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.OrganizationUser,
            member.Id,
            WorkBoardActions.ManageGrants,
            GrantScopeKind.Organization,
            null,
            cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(
                "A delegable organization grant-management grant is required.");
        return member;
    }

    private async Task ValidateSubjectAsync(
        Guid organizationId,
        GrantSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var exists = subjectKind switch
        {
            GrantSubjectKind.OrganizationUser => await db.CoreOrganizationUsers.AnyAsync(x =>
                x.Id == subjectId && x.OrganizationId == organizationId && x.IsActive,
                cancellationToken),
            GrantSubjectKind.AgentInstallation => await db.AgentInstallations.AnyAsync(x =>
                x.Id == subjectId &&
                x.BusinessId == organizationId.ToString() &&
                x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active,
                cancellationToken),
            GrantSubjectKind.AutomationIdentity => false,
            _ => false
        };
        if (!exists)
            throw new ArgumentException("The grant subject is not active in this organization.");
    }

    private async Task<IReadOnlyList<WorkBoardGrantResponse>> ListSubjectAsync(
        Guid organizationId,
        Guid? boardId,
        GrantScopeKind scopeKind,
        GrantSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == subjectKind &&
                x.SubjectId == subjectId &&
                x.ScopeKind == scopeKind &&
                x.ScopeId == boardId &&
                x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .OrderBy(x => x.Action)
            .Select(x => new WorkBoardGrantResponse(
                x.Id, x.SubjectKind.ToString(), x.SubjectId, x.Action,
                x.CanDelegate, x.Revision, x.GrantedAt, x.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<WorkBoardGrantResponse>> ListScopeAsync(
        Guid organizationId,
        GrantScopeKind scopeKind,
        Guid? scopeId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId &&
                x.ScopeKind == scopeKind &&
                x.ScopeId == scopeId &&
                x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .OrderBy(x => x.SubjectKind)
            .ThenBy(x => x.SubjectId)
            .ThenBy(x => x.Action)
            .Select(x => new WorkBoardGrantResponse(
                x.Id, x.SubjectKind.ToString(), x.SubjectId, x.Action,
                x.CanDelegate, x.Revision, x.GrantedAt, x.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    private Task WriteAuditAsync(
        Guid organizationId,
        Guid? boardId,
        OrganizationUser member,
        string action,
        object metadata,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            action,
            "Security",
            "Inbound",
            "Completed",
            organizationId,
            boardId.HasValue ? "WorkBoard" : "Organization",
            boardId ?? organizationId,
            action,
            JsonSerializer.Serialize(metadata),
            Actor: new AuditActor(
                "Human", true, member.ApplicationUserId, member.Id, member.DisplayName)),
            cancellationToken);
}
