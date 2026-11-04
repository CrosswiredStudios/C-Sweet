using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private async Task RequireImportedManagerTargetAsync(Guid org, Guid? projectId, string? role, CancellationToken ct)
    {
        var project = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId && x.OrganizationId == org, ct);
        var profile = await db.ImportedProjectProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.OrganizationId == org && x.Status == "Accepted", ct);
        var accepted = profile is null ? null : System.Text.Json.JsonSerializer.Deserialize<CSweet.Contracts.PersonalAssistant.RepositoryProjectBrief>(profile.AcceptedJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var expected = accepted?.Category switch { "Software" => "software-product-manager", "VideoGame" => "game-producer", _ => null };
        if (project is null || project.Status != WorkstreamStatus.Proposed || project.AccountableManagerOrganizationUserId.HasValue || expected is null || expected != role ||
            !await db.ProjectRepositoryAssociations.AnyAsync(x => x.ProjectId == projectId && x.OrganizationId == org, ct))
            throw new InvalidOperationException("The imported project or its accepted category changed. Review the manager hire again before installing or assigning an employee.");
    }

    private async Task AssignImportedProjectManagerAsync(Guid org, Guid projectId, Guid employeeId, CancellationToken ct)
    {
        var project = await db.Workstreams.SingleAsync(x => x.OrganizationId == org && x.Id == projectId, ct);
        if (project.AccountableManagerOrganizationUserId == employeeId) return;
        if (project.Status != WorkstreamStatus.Proposed || project.AccountableManagerOrganizationUserId.HasValue ||
            !await db.ProjectRepositoryAssociations.AnyAsync(x => x.OrganizationId == org && x.ProjectId == projectId, ct))
            throw new InvalidOperationException("The imported project no longer needs this manager assignment. Review the current project before completing the hire.");
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == org && x.Id == employeeId && x.IsActive && x.ArchivedAt == null, ct);
        if (manager.EmployeeType != EmployeeType.Agent || manager.AgentInstallationId is null)
            throw new InvalidOperationException("An imported project manager hire must be an active manager agent.");
        var setup = new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System));
        var profile = await db.ImportedProjectProfiles.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Status == "Accepted", ct);
        var accepted = profile is null ? null : System.Text.Json.JsonSerializer.Deserialize<CSweet.Contracts.PersonalAssistant.RepositoryProjectBrief>(profile.AcceptedJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var expectedRole = accepted?.Category switch { "Software" => "software-product-manager", "VideoGame" => "game-producer", _ => null };
        if (expectedRole is null || !await setup.HasRoleAsync(manager, expectedRole, ct))
            throw new InvalidOperationException("The hired manager must match the imported project's accepted category.");
        await setup.ReserveManagerAsync(org, employeeId, projectId, null, ct);
        project.AccountableManagerOrganizationUserId = employeeId;
        project.Revision++; project.UpdatedAt = DateTimeOffset.UtcNow;
        BusinessSuggestionService.Queue(db, org, AppRealtimeEvents.ProjectImportChanged, projectId);
        await db.SaveChangesAsync(ct);
    }
}
