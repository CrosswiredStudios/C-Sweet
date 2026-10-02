using CSweet.Domain.Core;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    /// <summary>Team membership sources that record a hire made for that team's delivery.</summary>
    public static readonly IReadOnlySet<string> HireMembershipSources =
        new HashSet<string>(["HiringWorkflow", "HiringRecommendation"], StringComparer.Ordinal);

    /// <summary>
    /// Makes an employee hired into a team a participant of the team's project. A staffing hire is made for
    /// that team's delivery, so the hire is the explicit decision to join its project; no separate membership
    /// step is needed. Hiring only joins the team, and project boards can be created before or after the hire,
    /// so this runs both when the hire joins the team and before work is dispatched to it.
    /// It never hires, moves, or restores anyone: a participant a human removed stays removed, and a person
    /// already working on another active project is left for an explicit decision.
    /// When <paramref name="projectId"/> is null, the team must have exactly one active project.
    /// Callers hold the organization transaction lock. Returns whether the employee is an active participant.
    /// </summary>
    public async Task<bool> EnrollHiredTeamMemberAsync(Guid org, Guid teamId, Guid employeeId, Guid? projectId, CancellationToken ct)
    {
        var membership = await db.TeamMemberships.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == org && x.TeamId == teamId && x.OrganizationUserId == employeeId && x.EndedAt == null, ct);
        if (membership is null || !HireMembershipSources.Contains(membership.SourceType)) return false;
        var person = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == org && x.Id == employeeId && x.IsActive && x.ArchivedAt == null, ct);
        if (person is null) return false;

        var assigned = db.WorkstreamTeamAssignments.Where(x => x.OrganizationId == org && x.TeamId == teamId && x.EndsAt == null)
            .Select(x => x.WorkstreamId);
        var projects = await db.Workstreams.Where(x => x.OrganizationId == org && assigned.Contains(x.Id) &&
                (x.Status == WorkstreamStatus.Approved || x.Status == WorkstreamStatus.Active) &&
                (projectId == null || x.Id == projectId))
            .ToListAsync(ct);
        if (projects.Count != 1) return false;
        var project = projects[0];

        var participant = await db.ProjectParticipants.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkstreamId == project.Id && x.OrganizationUserId == employeeId, ct);
        if (participant?.RemovedAt is not null) return false;
        if (participant is null && await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == org &&
                x.OrganizationUserId == employeeId && x.WorkstreamId != project.Id && x.RemovedAt == null &&
                db.Workstreams.Any(p => p.Id == x.WorkstreamId && p.Status != WorkstreamStatus.Completed && p.Status != WorkstreamStatus.Cancelled), ct))
            return false;

        var actorId = project.AccountableManagerOrganizationUserId
            ?? await db.OrganizationTeams.Where(x => x.Id == teamId).Select(x => (Guid?)x.LeadOrganizationUserId).SingleOrDefaultAsync(ct);
        var actor = actorId is null ? null : await db.CoreOrganizationUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == actorId, ct);
        if (actor is null) return false;

        var board = await db.WorkBoards.SingleOrDefaultAsync(x => x.OrganizationId == org && x.WorkstreamId == project.Id &&
            x.TeamId == teamId && x.ArchivedAt == null, ct);
        if (board is null)
        {
            // Board grants are added when the project's board exists; the participation itself is recorded now.
            if (participant is null)
                db.ProjectParticipants.Add(new() { OrganizationId = org, WorkstreamId = project.Id, OrganizationUserId = person.Id,
                    AddedByOrganizationUserId = actor.Id, JoinedAt = clock.GetUtcNow() });
            return true;
        }
        if (participant is null)
        {
            var added = await ApplyParticipantsAsync(project, board, [person], actor, ct);
            QueueProjectAssignmentChanges(project, board.Id, teamId, added, []);
            return true;
        }
        // Enrolled before the board existed: add the missing board access, but never restore a revoked grant.
        var subject = person.AgentInstallationId ?? person.Id;
        var kind = person.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
        var existing = await db.ScopedActionGrants.Where(x => x.OrganizationId == org && x.SubjectId == subject && x.SubjectKind == kind &&
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id).Select(x => x.Action).ToListAsync(ct);
        foreach (var action in ParticipantBoardActions(project, person).Except(existing, StringComparer.Ordinal))
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, SubjectKind = kind, SubjectId = subject, Action = action,
                ScopeKind = GrantScopeKind.Board, ScopeId = board.Id, GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                GrantedBySubjectId = actor.Id, GrantedAt = clock.GetUtcNow() });
        return true;
    }
}
