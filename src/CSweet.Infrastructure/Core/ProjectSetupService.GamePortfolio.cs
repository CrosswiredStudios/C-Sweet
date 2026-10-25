using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    // Sharing is authorized by the approved game's explicit studio-team assignment, not a role title.
    private async Task<bool> IsSharedGameTeamParticipantAsync(Workstream project, OrganizationUser person, CancellationToken ct)
    {
        if (project.ProfileKey != "video-game-production.v2" || project.ProfileVersion is not >= 7 ||
            project.Status is not (WorkstreamStatus.Approved or WorkstreamStatus.Active) ||
            person.OrganizationId != project.OrganizationId || !person.IsActive || person.ArchivedAt is not null)
            return false;
        var now = clock.GetUtcNow();
        var supervisors = await db.WorkstreamSupervisionAssignments.Where(x => x.OrganizationId == project.OrganizationId &&
            x.WorkstreamId == project.Id && x.RoleKey == "game-director" && x.StartsAt <= now &&
            (x.EndsAt == null || x.EndsAt > now)).Select(x => x.SupervisorOrganizationUserId).ToListAsync(ct);
        var directors = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == project.OrganizationId &&
            supervisors.Contains(x.Id) && x.IsActive && x.ArchivedAt == null).ToListAsync(ct);
        var validDirector = false;
        foreach (var director in directors)
            validDirector |= await HasRoleAsync(director, "game-director", ct);
        if (!validDirector) return false;
        return await (from assignment in db.WorkstreamTeamAssignments
                      join membership in db.TeamMemberships on assignment.TeamId equals membership.TeamId
                      join team in db.OrganizationTeams on assignment.TeamId equals team.Id
                      where assignment.OrganizationId == project.OrganizationId && assignment.WorkstreamId == project.Id &&
                            assignment.StartsAt <= now && (assignment.EndsAt == null || assignment.EndsAt > now) &&
                            team.ArchivedAt == null && membership.OrganizationId == project.OrganizationId &&
                            membership.OrganizationUserId == person.Id && membership.JoinedAt <= now && membership.EndedAt == null
                      select membership.Id).AnyAsync(ct);
    }
}
