using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    public async Task<ProjectSetupResult> ChangeStatusAsync(OrganizationUser actor, Guid id, ChangeProjectStatusRequest request, CancellationToken ct)
    {
        var project = await RequireManagerAsync(actor, id, ct);
        if (project.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The project changed. Refresh before changing its status.");
        var next = request.Status switch { "Active" => WorkstreamStatus.Active, "Completed" => WorkstreamStatus.Completed, "Cancelled" => WorkstreamStatus.Cancelled, _ => throw new ArgumentException("Choose Active, Completed or Cancelled.") };
        var binding = await db.ProjectDeliveryBindings.SingleAsync(x => x.WorkstreamId == id, ct);
        if (next == WorkstreamStatus.Active) await ReserveManagerAsync(actor.OrganizationId, project.AccountableManagerOrganizationUserId!.Value, id, null, ct);
        else
        {
            if (next == WorkstreamStatus.Completed && await db.CoreWorkTasks.AnyAsync(x => x.BoardId == binding.BoardId && x.ArchivedAt == null && x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled, ct))
                throw new InvalidOperationException("Finish or cancel the project's open tickets before completing it.");
            foreach (var reservation in await db.ProjectManagerReservations.Where(x => x.WorkstreamId == id).ToListAsync(ct)) db.ProjectManagerReservations.Remove(reservation);
        }
        project.Status = next; project.Revision++; project.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await RefreshIntakesAsync(actor.OrganizationId, id, ct); QueueProject(project); await db.SaveChangesAsync(ct);
        return new(id, binding.BoardId, project.Revision);
    }
    private async Task<Workstream> RequireManagerAsync(OrganizationUser actor, Guid id, CancellationToken ct)
    {
        var project = await db.Workstreams.SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Project not found.");
        if (actor.EmployeeType != EmployeeType.Human || (actor.PermissionLevel < OrganizationPermissionLevel.Manager && project.AccountableManagerOrganizationUserId != actor.Id))
            throw new UnauthorizedAccessException("Only an authorized human project manager can change project membership.");
        return project;
    }
    public async Task<ProjectMembersDetail> MembersAsync(OrganizationUser actor, Guid id, CancellationToken ct)
    {
        var project = await RequireManagerAsync(actor, id, ct);
        var binding = await db.ProjectDeliveryBindings.SingleOrDefaultAsync(x => x.WorkstreamId == id, ct);
        var teamId = binding?.TeamId ?? await db.WorkstreamTeamAssignments.Where(x => x.WorkstreamId == id && x.EndsAt == null).Select(x => x.TeamId).FirstOrDefaultAsync(ct);
        return new(id, project.Name, project.AccountableManagerOrganizationUserId!.Value, teamId,
            await db.ProjectParticipants.Where(x => x.WorkstreamId == id && x.RemovedAt == null).Select(x => x.OrganizationUserId).ToListAsync(ct), project.Revision);
    }
    public async Task<ProjectSetupResult> UpdateMembersAsync(OrganizationUser actor, Guid id, UpdateProjectMembersRequest request, CancellationToken ct)
    {
        var project = await RequireManagerAsync(actor, id, ct);
        if (request.ExpectedRevision != project.Revision) throw new DbUpdateConcurrencyException("The project changed. Refresh before saving membership.");
        if (request.MemberIds.Count == 0 || request.MemberIds.Count > 100) throw new ArgumentException("Select between 1 and 100 participants.");
        ProjectIntake? intake = null;
        if (request.IntakeId.HasValue)
        {
            intake = await AuthorizedIntakeAsync(actor, request.IntakeId.Value, ct);
            if (intake.Revision != request.IntakeRevision || intake.ProjectId != id || intake.Status is "Cancelled" or "Started")
                throw new DbUpdateConcurrencyException("The membership link is no longer current. Open the latest project request.");
        }
        var ids = request.MemberIds.Append(request.ManagerId).Distinct().ToArray();
        var people = await ValidatePeopleAsync(actor.OrganizationId, ids, request.ManagerId, ct);
        var binding = await db.ProjectDeliveryBindings.SingleOrDefaultAsync(x => x.WorkstreamId == id, ct);
        if (binding is null)
        {
            var existingTeam = await db.WorkstreamTeamAssignments.Where(x => x.WorkstreamId == id && x.EndsAt == null).Select(x => (Guid?)x.TeamId).SingleOrDefaultAsync(ct);
            var selectedTeam = await ResolveTeamAsync(actor.OrganizationId, existingTeam, people, actor, project.Name, ct);
            if (!existingTeam.HasValue) db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = actor.OrganizationId, WorkstreamId = id, TeamId = selectedTeam.Id, StartsAt = clock.GetUtcNow() });
            var existingBoard = await db.WorkBoards.SingleOrDefaultAsync(x => x.WorkstreamId == id && x.ArchivedAt == null, ct) ?? CreateBoard(project, selectedTeam.Id, request.ManagerId);
            if (existingBoard.TeamId != selectedTeam.Id) throw new InvalidOperationException("The board and project team do not match. Correct the board association before assigning members.");
            binding = new() { WorkstreamId = id, OrganizationId = actor.OrganizationId, BoardId = existingBoard.Id, TeamId = selectedTeam.Id, CreationKey = $"membership-setup:{id:N}" };
            db.ProjectDeliveryBindings.Add(binding);
            await db.SaveChangesAsync(ct);
        }
        await ResolveTeamAsync(actor.OrganizationId, binding.TeamId, people, actor, project.Name, ct);
        await ReserveManagerAsync(actor.OrganizationId, request.ManagerId, id, intake?.Id, ct);
        foreach (var previous in await db.ProjectManagerReservations.Where(x => x.WorkstreamId == id && x.OrganizationUserId != request.ManagerId).ToListAsync(ct))
            db.ProjectManagerReservations.Remove(previous);
        var board = await db.WorkBoards.SingleAsync(x => x.Id == binding.BoardId, ct);
        foreach (var removed in await db.ProjectParticipants.Where(x => x.WorkstreamId == id && x.RemovedAt == null && !ids.Contains(x.OrganizationUserId)).ToListAsync(ct))
        {
            removed.RemovedAt = clock.GetUtcNow(); removed.Revision++;
            var user = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == removed.OrganizationUserId, ct);
            var subject = user.AgentInstallationId ?? user.Id;
            foreach (var grant in await db.ScopedActionGrants.Where(x => x.OrganizationId == actor.OrganizationId && x.SubjectId == subject && x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id && x.RevokedAt == null).ToListAsync(ct))
            { grant.RevokedAt = clock.GetUtcNow(); grant.Revision++; }
        }
        project.AccountableManagerOrganizationUserId = request.ManagerId;
        await ApplyParticipantsAsync(project, board, people, actor, ct);
        board.ManagerOrganizationUserId = request.ManagerId;
        project.Revision++; board.Revision++; binding.Revision++;
        project.UpdatedAt = clock.GetUtcNow(); board.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await RefreshIntakesAsync(actor.OrganizationId, id, ct);
        QueueProject(project);
        await db.SaveChangesAsync(ct);
        return new(id, board.Id, project.Revision);
    }
}
