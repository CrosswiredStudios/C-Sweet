using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    /// <summary>
    /// Gives a project's people their access to a project board created outside <see cref="PrepareDeliveryAsync"/>,
    /// for example by a Producer who manages the project but does not lead its team. Without this the accountable
    /// manager owns the board but holds no project delivery grant, so every delivery read is denied and staffing never
    /// starts, and participants enrolled before the board existed hold no board or delivery access.
    /// The accountable manager is enrolled as a participant. Every active participant receives the board and delivery
    /// grants that <see cref="ApplyParticipantsAsync"/> would have given them. A participant a human removed, a revoked
    /// grant, and a manager already working on another active project are left for an explicit decision.
    /// Callers hold the organization transaction lock and save the changes. Returns the employees newly enrolled.
    /// </summary>
    public async Task<IReadOnlyList<OrganizationUser>> ReconcileProjectBoardAccessAsync(WorkBoard board, CancellationToken ct)
    {
        if (board.WorkstreamId is not { } projectId || board.ArchivedAt.HasValue) return [];
        var project = await db.Workstreams.SingleOrDefaultAsync(x => x.OrganizationId == board.OrganizationId && x.Id == projectId, ct);
        if (project is null || project.Status is not (WorkstreamStatus.Approved or WorkstreamStatus.Active)) return [];

        var participantIds = db.ProjectParticipants.Where(x => x.WorkstreamId == project.Id && x.RemovedAt == null)
            .Select(x => x.OrganizationUserId);
        var people = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == project.OrganizationId &&
            participantIds.Contains(x.Id) && x.IsActive && x.ArchivedAt == null).ToListAsync(ct);

        var manager = project.AccountableManagerOrganizationUserId is { } managerId
            ? await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == project.OrganizationId &&
                x.Id == managerId && x.IsActive && x.ArchivedAt == null, ct)
            : null;
        var added = new List<OrganizationUser>();
        if (manager is not null && people.All(x => x.Id != manager.Id) &&
            !await db.ProjectParticipants.AnyAsync(x => x.WorkstreamId == project.Id && x.OrganizationUserId == manager.Id, ct) &&
            !await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == project.OrganizationId &&
                x.OrganizationUserId == manager.Id && x.WorkstreamId != project.Id && x.RemovedAt == null &&
                db.Workstreams.Any(p => p.Id == x.WorkstreamId && p.Status != WorkstreamStatus.Completed && p.Status != WorkstreamStatus.Cancelled), ct))
        {
            db.ProjectParticipants.Add(new() { OrganizationId = project.OrganizationId, WorkstreamId = project.Id,
                OrganizationUserId = manager.Id, AddedByOrganizationUserId = manager.Id, JoinedAt = clock.GetUtcNow() });
            people.Add(manager);
            added.Add(manager);
        }

        var grantor = manager?.Id ?? board.ManagerOrganizationUserId;
        if (grantor is null) return added;
        foreach (var person in people)
            await AddMissingParticipantAccessAsync(project, board.Id, person, grantor.Value, ct);
        return added;
    }

    /// <summary>
    /// Adds the board and project delivery grants a participant should hold and does not. An existing grant row,
    /// including a revoked one, is left as it is: automatic enrollment never restores authority a human took away.
    /// </summary>
    internal async Task AddMissingParticipantAccessAsync(Workstream project, Guid boardId, OrganizationUser person, Guid grantorId, CancellationToken ct)
    {
        var subject = person.AgentInstallationId ?? person.Id;
        var kind = person.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
        var existing = await db.ScopedActionGrants.Where(x => x.OrganizationId == project.OrganizationId && x.SubjectId == subject &&
                x.SubjectKind == kind && ((x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId) ||
                    (x.ScopeKind == GrantScopeKind.Workstream && x.ScopeId == project.Id)))
            .Select(x => new { x.ScopeKind, x.Action }).ToListAsync(ct);
        var pending = db.ScopedActionGrants.Local.Where(x => x.OrganizationId == project.OrganizationId && x.SubjectId == subject &&
                x.SubjectKind == kind && ((x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId) ||
                    (x.ScopeKind == GrantScopeKind.Workstream && x.ScopeId == project.Id)))
            .Select(x => new { x.ScopeKind, x.Action });
        var held = existing.Concat(pending).Select(x => (x.ScopeKind, x.Action)).ToHashSet();
        var now = clock.GetUtcNow();
        void Add(GrantScopeKind scope, Guid scopeId, string action)
        {
            if (!held.Add((scope, action))) return;
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, SubjectKind = kind,
                SubjectId = subject, Action = action, ScopeKind = scope, ScopeId = scopeId,
                GrantedBySubjectKind = GrantSubjectKind.OrganizationUser, GrantedBySubjectId = grantorId, GrantedAt = now });
        }
        foreach (var action in ParticipantBoardActions(project, person)) Add(GrantScopeKind.Board, boardId, action);
        foreach (var action in await ParticipantDeliveryActionsAsync(project, person, ct)) Add(GrantScopeKind.Workstream, project.Id, action);
    }
}
