using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class WebPreviewTriageService(CSweetDbContext db, WebPreviewGrantService grants, TimeProvider clock)
{
    public async Task ConfigureAsync(Guid organizationId, Guid applicationUserId, Guid projectId, ConfigurePreviewTriage input, CancellationToken token)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner, token)) throw new UnauthorizedAccessException("Only the business owner can assign preview triage.");
        var actor = await grants.RequireActorAsync(organizationId, input.InstallationId, WebPreviewTriageCapabilities.ReadFinding, token);
        await grants.RequireWorkstreamAsync(organizationId, actor.Id, projectId, token);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x => x.Id == input.BoardId && x.OrganizationId == organizationId &&
            x.WorkstreamId == projectId && x.ArchivedAt == null, token)) throw new UnauthorizedAccessException("Choose a board belonging to this project.");
        if (!await db.CoreWorkTasks.AsNoTracking().AnyAsync(x => x.Id == input.ParentItemId && x.OrganizationId == organizationId && x.BoardId == input.BoardId, token))
            throw new UnauthorizedAccessException("Choose an existing parent ticket on the triage board.");
        var manifestJson = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == input.InstallationId)
            .Select(x => x.PackageVersion!.ManifestJson).SingleAsync(token);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (manifest?.Provides.Any(x => x.Name == WebPreviewTriageCapabilities.Triage) != true)
            throw new ArgumentException("The employee must implement preview finding triage.");
        var route = await db.WebPreviewTriageRoutes.SingleOrDefaultAsync(x => x.ProjectId == projectId, token);
        if (route is null) { route = new() { ProjectId = projectId, OrganizationId = organizationId }; db.WebPreviewTriageRoutes.Add(route); }
        if (route.OrganizationId != organizationId) throw new UnauthorizedAccessException();
        route.InstallationId = input.InstallationId; route.BoardId = input.BoardId; route.ParentItemId = input.ParentItemId; route.Revision++;
        await db.SaveChangesAsync(token);
    }
    public async Task<WebPreviewFindingRecord> RequireFindingAsync(Guid organizationId, Guid installationId, Guid findingId, string capability, CancellationToken token)
    {
        var actor = await grants.RequireActorAsync(organizationId, installationId, capability, token);
        var finding = await db.WebPreviewFindings.SingleOrDefaultAsync(x => x.Id == findingId && x.OrganizationId == organizationId, token)
            ?? throw new UnauthorizedAccessException("The finding is unavailable.");
        await grants.RequireWorkstreamAsync(organizationId, actor.Id, finding.ProjectId, token);
        var job = await db.WebPreviewJobs.AsNoTracking().SingleAsync(x => x.Id == finding.PreviewId, token);
        if (job.InstallationId != installationId && !await db.WebPreviewTriageRoutes.AsNoTracking().AnyAsync(x =>
            x.ProjectId == finding.ProjectId && x.OrganizationId == organizationId && x.InstallationId == installationId, token))
            throw new UnauthorizedAccessException("The finding is outside this employee's assignment.");
        return finding;
    }
    public async Task<PreviewFindingDetail> ReadAsync(Guid organizationId, Guid installationId, Guid findingId, CancellationToken token)
    {
        var finding = await RequireFindingAsync(organizationId, installationId, findingId, WebPreviewTriageCapabilities.ReadFinding, token);
        if (finding.RetainUntil <= clock.GetUtcNow()) throw new InvalidOperationException("Diagnostic retention expired; use the linked ticket's copied evidence.");
        var job = await db.WebPreviewJobs.AsNoTracking().SingleAsync(x => x.Id == finding.PreviewId, token);
        var route = await db.WebPreviewTriageRoutes.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == finding.ProjectId && x.OrganizationId == organizationId, token);
        var parent = route is null ? null : await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == route.ParentItemId &&
            x.OrganizationId == organizationId && x.BoardId == route.BoardId, token);
        return new(finding.Id, finding.PreviewId, finding.ProjectId, job.RepositoryId, finding.BuildId, finding.Fingerprint,
            JsonSerializer.Deserialize<PreviewDiagnostic>(finding.EvidenceJson, PreviewJson.Options)!, finding.BoardId ?? route?.BoardId, finding.TicketId, parent?.Id, parent?.TypeKey);
    }
    public async Task DispatchAsync(AgentWorkInbox inbox, CancellationToken token)
    {
        foreach (var findingId in await db.WebPreviewFindings.Where(x => x.TriageWorkId == null && x.TicketId == null && x.RetainUntil > clock.GetUtcNow())
            .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(64).ToListAsync(token))
        {
            var finding = await db.WebPreviewFindings.SingleAsync(x => x.Id == findingId, token);
            var route = await db.WebPreviewTriageRoutes.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == finding.ProjectId && x.OrganizationId == finding.OrganizationId, token);
            if (route is null) continue;
            try
            {
                await RequireFindingAsync(finding.OrganizationId, route.InstallationId, finding.Id, WebPreviewTriageCapabilities.ReadFinding, token);
                var work = await inbox.EnqueueAsync(finding.OrganizationId.ToString("D"), route.InstallationId, AgentWorkKind.Capability,
                    WebPreviewTriageCapabilities.Triage, JsonSerializer.SerializeToElement(new PreviewTriageAssignment(finding.Id, finding.ProjectId, route.BoardId), PreviewJson.Options),
                    "web-preview-finding:" + finding.Id.ToString("N"), finding.RetainUntil, sourceType: "web-preview-finding", sourceId: finding.Id.ToString("D"), cancellationToken: token);
                finding.TriageWorkId = work.Id; finding.TriageInstallationId = route.InstallationId; finding.BoardId = route.BoardId; finding.Revision++;
                await db.SaveChangesAsync(token);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or JsonException) { db.ChangeTracker.Clear(); }
        }
    }
}
