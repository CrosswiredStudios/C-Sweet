using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed record WebPreviewDashboard(bool Enabled, bool Owner, IReadOnlyList<WebPreviewDashboardItem> Previews,
    IReadOnlyList<WebPreviewChoice> Projects, IReadOnlyList<WebPreviewChoice> Boards, IReadOnlyList<WebPreviewChoice> TriageEmployees, IReadOnlyList<WebPreviewChoice> Parents);
public sealed record WebPreviewDashboardItem(Guid Id, Guid ProjectId, string Project, string Phase, DateTimeOffset ExpiresAt,
    string? AccessReference, string? FailureCode, bool TeardownConfirmed, int FindingCount);
public sealed record WebPreviewChoice(Guid Id, string Name, Guid? ProjectId = null);

public sealed class WebPreviewManagementService(CSweetDbContext db, WebPreviewGrantService grants, WebPreviewExecutionService execution, TimeProvider clock)
{
    private Task<OrganizationUser> MemberAsync(Guid organizationId, Guid applicationUserId, CancellationToken token) =>
        db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId &&
            x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human, token);
    public async Task<bool> EnabledAsync(Guid organizationId, Guid applicationUserId, CancellationToken token)
    {
        await MemberAsync(organizationId, applicationUserId, token);
        var ids = await db.AgentInstallations.AsNoTracking().Where(x => x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
            x.PackageVersion!.AgentId == WebPreviewGrantService.PluginId).Select(x => x.Id).ToListAsync(token);
        foreach (var id in ids)
            try { await grants.RequireProviderAsync(organizationId, id, token); return true; } catch (UnauthorizedAccessException) { }
        return false;
    }
    public async Task<WebPreviewDashboard> ReadAsync(Guid organizationId, Guid applicationUserId, CancellationToken token)
    {
        var member = await MemberAsync(organizationId, applicationUserId, token);
        var owner = member.PermissionLevel == OrganizationPermissionLevel.Owner;
        var providerIds = await db.AgentInstallations.AsNoTracking().Where(x => x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
            x.PackageVersion!.AgentId == WebPreviewGrantService.PluginId).Select(x => x.Id).ToListAsync(token);
        var enabled = false;
        foreach (var id in providerIds)
            try { await grants.RequireProviderAsync(organizationId, id, token); enabled = true; break; } catch (UnauthorizedAccessException) { }
        var projects = new List<WebPreviewChoice>();
        foreach (var project in await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId).OrderBy(x => x.Name).Take(500).ToListAsync(token))
        {
            try { if (!owner) await grants.RequireWorkstreamAsync(organizationId, member.Id, project.Id, token); projects.Add(new(project.Id, project.Name)); }
            catch (UnauthorizedAccessException) { }
        }
        var projectIds = projects.Select(x => x.Id).ToArray();
        var jobs = await db.WebPreviewJobs.AsNoTracking().Where(x => x.OrganizationId == organizationId && projectIds.Contains(x.WorkstreamId))
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
        var previews = new List<WebPreviewDashboardItem>();
        foreach (var job in jobs)
            previews.Add(new(job.Id, job.WorkstreamId, projects.Single(x => x.Id == job.WorkstreamId).Name, job.Phase, job.ExpiresAt,
                job.Phase == "Ready" && job.ExpiresAt > clock.GetUtcNow() ? job.AccessReference : null, job.FailureCode, job.TeardownConfirmedAt is not null,
                await db.WebPreviewFindings.CountAsync(x => x.OrganizationId == organizationId && x.PreviewId == job.Id, token)));
        var boards = owner ? await db.WorkBoards.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.ArchivedAt == null && x.WorkstreamId != null &&
            projectIds.Contains(x.WorkstreamId.Value)).Select(x => new WebPreviewChoice(x.Id, x.Name, x.WorkstreamId)).ToListAsync(token) : [];
        var employees = new List<WebPreviewChoice>();
        if (owner)
            foreach (var actor in await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive && x.ArchivedAt == null && x.AgentInstallationId != null).ToListAsync(token))
                try { await grants.RequireActorAsync(organizationId, actor.AgentInstallationId!.Value, CSweet.WebHost.Contracts.WebPreviewTriageCapabilities.ReadFinding, token);
                    employees.Add(new(actor.AgentInstallationId.Value, actor.DisplayName)); } catch (UnauthorizedAccessException) { }
        var boardIds = boards.Select(x => x.Id).ToArray();
        var parents = owner ? await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.BoardId != null && boardIds.Contains(x.BoardId.Value))
            .OrderBy(x => x.Title).Take(1000).Select(x => new WebPreviewChoice(x.Id, x.Title, x.BoardId)).ToListAsync(token) : [];
        return new(enabled, owner, previews, projects, boards, employees, parents);
    }
    public async Task StopAsync(Guid organizationId, Guid applicationUserId, Guid previewId, CancellationToken token)
    {
        var member = await MemberAsync(organizationId, applicationUserId, token);
        if (member.PermissionLevel != OrganizationPermissionLevel.Owner) throw new UnauthorizedAccessException("The business owner can stop a preview.");
        var job = await db.WebPreviewJobs.SingleOrDefaultAsync(x => x.Id == previewId && x.OrganizationId == organizationId, token)
            ?? throw new UnauthorizedAccessException();
        if (job.TeardownConfirmedAt is not null || job.Phase == "Stopping") return;
        job.Phase = "Stopping"; job.AccessReference = null; job.UpdatedAt = clock.GetUtcNow(); job.Revision++;
        await execution.CancelPendingAsync(job, token); execution.Queue(job, "stop"); await db.SaveChangesAsync(token);
    }
}
