using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

/// <summary>Business-scoped directory metadata shared by internal and connected repositories.
/// Call only after the caller's business membership has been authorized.</summary>
internal static class RepositoryDirectoryProjection
{
    public static async Task<IReadOnlyList<SourceControlRepositorySummary>> PopulateAsync(
        CSweetDbContext db, Guid business, IReadOnlyList<SourceControlRepositorySummary> repositories,
        CancellationToken ct)
    {
        if (repositories.Count == 0) return repositories;
        var ids = repositories.Select(r => r.Id).ToArray();
        var businessKey = business.ToString("D");
        var employees = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(u => u.OrganizationId == business)
            .Select(u => new
            {
                u.Id, u.DisplayName, u.EmployeeType, u.ApplicationUserId, u.AgentInstallationId,
                u.IsActive, u.ArchivedAt, u.PermissionLevel, Role = u.Role == null ? null : u.Role.Name,
                AgentEnabled = u.AgentInstallation != null && u.AgentInstallation.IsEnabled &&
                    u.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active &&
                    u.AgentInstallation.Scope == PluginInstallationScope.Organization &&
                    u.AgentInstallation.BusinessId == businessKey
            }).ToListAsync(ct);
        var policies = await (from policy in db.TeamRepositoryPolicies.AsNoTracking()
            join team in db.OrganizationTeams.AsNoTracking() on policy.TeamId equals team.Id
            where policy.OrganizationId == business && team.OrganizationId == business &&
                ids.Contains(policy.RepositoryId) && policy.DisabledAt == null && team.ArchivedAt == null
            select new { policy.RepositoryId, team.Id, team.Name }).ToListAsync(ct);
        var teamIds = policies.Select(p => p.Id).Distinct().ToArray();
        var memberships = await db.TeamMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == business && teamIds.Contains(m.TeamId) && m.EndedAt == null)
            .Select(m => new { m.TeamId, m.OrganizationUserId }).ToListAsync(ct);

        // Sharing a team does not establish a project link. Use explicit provisioning
        // and work-item assignments, including existing workspaces for historical work.
        var projectLinks = await (from request in db.RepositoryProvisioningRequests.AsNoTracking()
            join project in db.Workstreams.AsNoTracking() on request.WorkstreamId equals project.Id
            where request.OrganizationId == business && project.OrganizationId == business &&
                request.RepositoryId.HasValue && ids.Contains(request.RepositoryId.Value)
            select new { RepositoryId = request.RepositoryId!.Value, project.Id, project.Name }).ToListAsync(ct);
        var workLinks = await (from item in db.CoreWorkTasks.AsNoTracking()
            join board in db.WorkBoards.AsNoTracking() on item.BoardId equals board.Id
            join project in db.Workstreams.AsNoTracking() on board.WorkstreamId equals project.Id
            where item.OrganizationId == business && board.OrganizationId == business && project.OrganizationId == business &&
                (item.DevelopmentBriefJson != null || item.DeliverySpecificationJson != null)
            select new { item.DevelopmentBriefJson, item.DeliverySpecificationJson, project.Id, project.Name }).ToListAsync(ct);
        foreach (var link in workLinks)
        {
            var repositoryId = ReadRepositoryId(link.DeliverySpecificationJson) ?? ReadRepositoryId(link.DevelopmentBriefJson);
            if (repositoryId is { } id && ids.Contains(id)) projectLinks.Add(new { RepositoryId = id, link.Id, link.Name });
        }
        var workspaceProjects = await (from workspace in db.SourceControlWorkspaces.AsNoTracking()
            join item in db.CoreWorkTasks.AsNoTracking() on workspace.WorkItemId equals item.Id
            join board in db.WorkBoards.AsNoTracking() on item.BoardId equals board.Id
            join project in db.Workstreams.AsNoTracking() on board.WorkstreamId equals project.Id
            where workspace.OrganizationId == business && item.OrganizationId == business &&
                board.OrganizationId == business && project.OrganizationId == business && ids.Contains(workspace.RepositoryId)
            select new { workspace.RepositoryId, project.Id, project.Name }).Distinct().ToListAsync(ct);
        projectLinks.AddRange(workspaceProjects);

        // Project only the latest record per repository; audit payloads and provider
        // error details never enter the directory response.
        var activity = await db.SourceControlRepositories.AsNoTracking()
            .Where(r => r.OrganizationId == business && ids.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                Audit = db.AuditEvents.Where(e => e.OrganizationId == business && e.Category == "SourceControl" &&
                        e.EntityType == "SourceControlRepository" && e.EntityId == r.Id)
                    .OrderByDescending(e => e.Sequence)
                    .Select(e => new { e.EventType, e.OccurredAt, e.Outcome, e.ActorDisplayName, e.ActorKind,
                        e.ActorApplicationUserId, e.ActorInstallationId }).FirstOrDefault(),
                Workspace = db.SourceControlWorkspaces.Where(w => w.OrganizationId == business && w.RepositoryId == r.Id)
                    .OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id)
                    .Select(w => new { w.UpdatedAt, w.AgentInstallationId, w.Status }).FirstOrDefault(),
                Publication = db.SourceControlPublications.Where(p => p.OrganizationId == business && p.RepositoryId == r.Id)
                    .OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id)
                    .Select(p => new { p.UpdatedAt, p.Status, InstallationId = p.Workspace != null && p.Workspace.OrganizationId == business
                        ? (Guid?)p.Workspace.AgentInstallationId : null }).FirstOrDefault()
            }).ToListAsync(ct);
        var byRepository = activity.ToDictionary(a => a.Id);
        var projects = projectLinks.GroupBy(p => p.RepositoryId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<RepositoryProjectSummary>)g.DistinctBy(p => p.Id).OrderBy(p => p.Name)
                .Select(p => new RepositoryProjectSummary(p.Id, p.Name)).ToArray());
        var teamAccess = (from policy in policies
            join membership in memberships on policy.Id equals membership.TeamId
            select new { policy.RepositoryId, membership.OrganizationUserId, policy.Name })
            .ToLookup(p => (p.RepositoryId, p.OrganizationUserId));

        return repositories.Select(repository =>
        {
            var access = new List<RepositoryAccessSummary>();
            foreach (var employee in employees.Where(e => e.IsActive && e.ArchivedAt == null))
            {
                if (employee.EmployeeType == EmployeeType.Human && employee.ApplicationUserId.HasValue)
                    access.Add(new(employee.Id, employee.DisplayName, "Human", employee.Role,
                        employee.PermissionLevel >= OrganizationPermissionLevel.Manager ? "Admin" : "Read",
                        "Business membership"));
                else if (employee.EmployeeType == EmployeeType.Agent && employee.AgentEnabled)
                {
                    var teams = teamAccess[(repository.Id, employee.Id)].Select(t => t.Name).Distinct().Order().ToArray();
                    if (teams.Length > 0)
                        access.Add(new(employee.Id, employee.DisplayName, "Agent", employee.Role,
                            "Team access", string.Join(", ", teams)));
                }
            }
            var result = repository with
            {
                Projects = projects.GetValueOrDefault(repository.Id) ?? [],
                Access = access.OrderBy(a => a.EmployeeType == "Human").ThenBy(a => a.DisplayName).ToArray()
            };
            if (!byRepository.TryGetValue(repository.Id, out var latest)) return result;
            if (latest.Audit is { } audit)
            {
                var actor = employees.FirstOrDefault(e =>
                    (audit.ActorInstallationId.HasValue && e.AgentInstallationId == audit.ActorInstallationId) ||
                    (audit.ActorApplicationUserId.HasValue && e.ApplicationUserId == audit.ActorApplicationUserId));
                result = result with { LastEventType = audit.EventType, LastEventOccurredAt = audit.OccurredAt,
                    LastEventActor = audit.ActorDisplayName ?? actor?.DisplayName ?? audit.ActorKind, LastEventOutcome = audit.Outcome };
            }
            if (latest.Workspace is { } workspace && (result.LastEventOccurredAt is null || workspace.UpdatedAt > result.LastEventOccurredAt))
                result = result with { LastEventType = "SourceControl.Workspace." + workspace.Status,
                    LastEventOccurredAt = workspace.UpdatedAt, LastEventOutcome = null,
                    LastEventActor = employees.FirstOrDefault(e => e.AgentInstallationId == workspace.AgentInstallationId)?.DisplayName };
            if (latest.Publication is { } publication && (result.LastEventOccurredAt is null || publication.UpdatedAt >= result.LastEventOccurredAt))
                result = result with { LastEventType = "SourceControl.Publication." + publication.Status,
                    LastEventOccurredAt = publication.UpdatedAt, LastEventOutcome = null,
                    LastEventActor = publication.InstallationId.HasValue
                        ? employees.FirstOrDefault(e => e.AgentInstallationId == publication.InstallationId)?.DisplayName : null };
            return result;
        }).ToArray();
    }

    private static Guid? ReadRepositoryId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("repositoryId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var id)) return id;
        }
        catch (JsonException) { /* Incomplete historical briefs do not establish a project association. */ }
        return null;
    }
}
