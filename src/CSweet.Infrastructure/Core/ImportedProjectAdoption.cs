using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

public static class ImportedProjectAdoption
{
    public static async Task<bool> CanRequestInitialStaffingAsync(CSweetDbContext db, Guid org, Guid projectId, OrganizationUser requester, CancellationToken ct)
    {
        var project = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId && x.OrganizationId == org && x.Status == WorkstreamStatus.Proposed, ct);
        if (project is null || !await db.ProjectRepositoryAssociations.AnyAsync(x => x.ProjectId == projectId && x.OrganizationId == org, ct)) return false;
        if (project.AccountableManagerOrganizationUserId == requester.Id) return true;
        if (project.AccountableManagerOrganizationUserId.HasValue) return false;
        var profile = await db.ImportedProjectProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Status == "Accepted", ct);
        var accepted = profile is null ? null : JsonSerializer.Deserialize<CSweet.Contracts.PersonalAssistant.RepositoryProjectBrief>(profile.AcceptedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return accepted?.Category == "VideoGame" && requester.ReportsToOrganizationUserId.HasValue &&
            await db.CoreOrganizationUsers.AnyAsync(x => x.Id == requester.ReportsToOrganizationUserId && x.OrganizationId == org && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner, ct) &&
            await new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System)).HasRoleAsync(requester, "game-director", ct);
    }

    public static async Task<Workstream> RequireAsync(CSweetDbContext db, Guid org, W.WorkstreamPlanProposalV2Request request, CancellationToken ct)
    {
        var project = await db.Workstreams.SingleOrDefaultAsync(x => x.Id == request.ExistingProjectId && x.OrganizationId == org, ct);
        if (project is null || project.Status != WorkstreamStatus.Proposed || project.SourceProposalId.HasValue || project.Revision != request.ExpectedProjectRevision ||
            !await db.ProjectRepositoryAssociations.AnyAsync(x => x.ProjectId == project.Id && x.OrganizationId == org, ct))
            throw new InvalidOperationException("The imported project changed after setup was requested.");
        if (project.AccountableManagerOrganizationUserId == request.AccountableManagerOrganizationUserId) return project;
        if (project.AccountableManagerOrganizationUserId.HasValue) throw new UnauthorizedAccessException("The imported project already has a different manager.");
        var profile = await db.ImportedProjectProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == project.Id && x.OrganizationId == org && x.Status == "Accepted", ct);
        var accepted = profile is null ? null : JsonSerializer.Deserialize<CSweet.Contracts.PersonalAssistant.RepositoryProjectBrief>(profile.AcceptedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manager = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == request.AccountableManagerOrganizationUserId && x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct);
        var director = manager?.ReportsToOrganizationUserId is not { } directorId ? null : await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == directorId && x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct);
        var setup = new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System));
        if (accepted?.Category != "VideoGame" || manager is null || director is null || !await setup.HasRoleAsync(manager, "game-producer", ct) ||
            !await setup.HasRoleAsync(director, "game-director", ct) || !request.InitialSupervisors.Any(x => x.SupervisorOrganizationUserId == director.Id && x.RoleKey == "game-director"))
            throw new UnauthorizedAccessException("An unassigned game import requires its Producer and reporting Director in the reviewed setup plan.");
        return project;
    }
}
