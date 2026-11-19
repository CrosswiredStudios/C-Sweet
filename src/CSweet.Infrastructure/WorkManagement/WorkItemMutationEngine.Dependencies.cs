using System.Text.Json;
using System.Globalization;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    // Resource kinds are resolved from authorized current state, never from event payloads or model prose.
    private async Task<string> ReadDependencyVersionAsync(WorkTask item, Wire.PersonalTodoDependency dependency,
        CancellationToken token)
    {
        if (dependency.ResourceId == Guid.Empty || string.IsNullOrWhiteSpace(dependency.ObservedVersion))
            throw new ArgumentException("An exact supported dependency and observed version are required.");
        var context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(item.PersonalWorkContextJson ?? "null", JsonOptions);
        if (dependency.Kind == ResourceCapabilities.Changed && item.AssignedAgentInstallationId is Guid installation)
            return await new ResourceFulfillmentService(db, [], clock).ReadWaitPhaseAsync(item.OrganizationId, installation,
                dependency.ResourceId, context?.WorkstreamId, token);
        var readCapability = dependency.Kind switch {
            WorkforceEvents.Changed => PlatformCapabilities.TeamRosterReadV2,
            Wire.WorkstreamEventNames.DecisionDecidedV1 => PlatformCapabilities.DecisionRead,
            _ => null
        };
        if (readCapability is not null && !await db.HasRequiredCapabilityAsync(item.OrganizationId, item.AssignedAgentInstallationId, readCapability, token))
            throw new UnauthorizedAccessException("The waiting installation needs read access to its dependency.");
        if (dependency.Kind == WorkforceEvents.Changed)
        {
            if (context?.TeamId != dependency.ResourceId || context.WorkstreamId is not Guid project)
                throw new UnauthorizedAccessException("The roster wait must identify the waiting work's team and project.");
            var teamId = dependency.ResourceId;
            var revision = await db.OrganizationTeams.AsNoTracking().Where(t => t.Id == teamId && t.OrganizationId == item.OrganizationId &&
                t.ArchivedAt == null && db.WorkstreamTeamAssignments.Any(a => a.OrganizationId == t.OrganizationId &&
                    a.WorkstreamId == project && a.TeamId == teamId && a.EndsAt == null) &&
                (db.TeamMemberships.Any(m => m.OrganizationId == t.OrganizationId && m.TeamId == teamId &&
                    m.OrganizationUserId == item.AssignedEmployeeId && m.EndedAt == null) ||
                 db.Workstreams.Any(w => w.OrganizationId == t.OrganizationId && w.Id == project && w.AccountableManagerOrganizationUserId == item.AssignedEmployeeId) ||
                 db.WorkstreamSupervisionAssignments.Any(a => a.OrganizationId == t.OrganizationId && a.WorkstreamId == project &&
                    a.SupervisorOrganizationUserId == item.AssignedEmployeeId && a.EndsAt == null)))
                .Select(t => (long?)t.Revision).SingleOrDefaultAsync(token)
                ?? throw new UnauthorizedAccessException("The project roster is outside the waiting employee's scope.");
            return revision.ToString(CultureInfo.InvariantCulture);
        }
        if (dependency.Kind == Wire.WorkstreamEventNames.DecisionDecidedV1 && context?.WorkstreamId is Guid decisionProject)
            return await db.WorkstreamDecisions.AsNoTracking().Where(d => d.OrganizationId == item.OrganizationId &&
                d.WorkstreamId == decisionProject && d.Id == dependency.ResourceId &&
                d.RequestedByOrganizationUserId == item.AssignedEmployeeId && d.RequestedByInstallationId == item.AssignedAgentInstallationId)
                .Select(d => d.Status).SingleOrDefaultAsync(token)
                ?? throw new UnauthorizedAccessException("The waiting employee must own the referenced project decision.");
        if (dependency.Kind != Wire.PersonalTodoDependencyKinds.ResourceChange)
            throw new ArgumentException("Unsupported personal-work dependency kind.");
        var resource = await db.ResourceChangeRequests.SingleOrDefaultAsync(r => r.Id == dependency.ResourceId &&
            r.OrganizationId == item.OrganizationId && r.RequesterOrganizationUserId == item.AssignedEmployeeId &&
            r.RequesterInstallationId == item.AssignedAgentInstallationId, token)
            ?? throw new UnauthorizedAccessException("The waiting work does not own this dependency.");
        if (context is null || context.TeamId != resource.TeamId || context.WorkstreamId != resource.WorkstreamId)
            throw new UnauthorizedAccessException("The dependency must match the waiting work's team and project.");
        return resource.Status.ToString();
    }

    /// <summary>Reconciles referenced waits on resource mutation, defer and reconnect. Caller saves work and outbox together.</summary>
    internal async Task RecoverDependencyWaitsAsync(CancellationToken token, WorkTask? deferred = null, Guid? organizationId = null,
        (string Kind, Guid Id)? changed = null, string? committingVersion = null)
    {
        var offset = 0;
        while (true)
        {
            var candidates = db.CoreWorkTasks.Include(x => x.Board).Where(x => (!organizationId.HasValue || x.OrganizationId == organizationId) && x.ArchivedAt == null &&
                    x.Status == WorkTaskStatus.Running && x.NextReviewAt != null && x.ClaimEventId == null &&
                    x.ClaimExpiresAt == null && x.WaitingDependencyJson != null);
            if (changed is { } source)
            {
                var reference = source.Id.ToString("D");
                var match = JsonSerializer.Serialize(new { kind = source.Kind, resourceId = source.Id });
                candidates = db.Database.IsNpgsql()
                    ? candidates.Where(x => EF.Functions.JsonContains(x.WaitingDependencyJson!, match))
                    : candidates.Where(x => x.WaitingDependencyJson!.Contains(reference));
            }
            var waiting = await candidates.OrderBy(x => x.Id).Skip(offset).Take(HardOpenItemLimit).ToListAsync(token);
            offset += waiting.Count;
            var lastPage = waiting.Count < HardOpenItemLimit;
            if (deferred is not null && offset <= HardOpenItemLimit && waiting.All(x => x.Id != deferred.Id)) waiting.Add(deferred);
            foreach (var item in waiting)
            {
                if (item.Status != WorkTaskStatus.Running || item.ClaimEventId != null || item.ClaimExpiresAt != null ||
                    item.WaitingDependencyJson is null || item.Board is not { ArchivedAt: null } ||
                    item.AssignedAgentInstallationId is not { } installation) continue;
                Wire.PersonalTodoDependency? dependency;
                try
                {
                    dependency = JsonSerializer.Deserialize<Wire.PersonalTodoDependency>(item.WaitingDependencyJson, JsonOptions);
                    if (dependency is null || changed is { } target && (dependency.Kind != target.Kind || dependency.ResourceId != target.Id)) continue;
                    var current = await ReadDependencyVersionAsync(item, dependency, token);
                    if ((committingVersion ?? current) == dependency.ObservedVersion) continue;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or UnauthorizedAccessException) { continue; }
                var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(u => u.Id == item.AssignedEmployeeId &&
                    u.OrganizationId == item.OrganizationId && u.AgentInstallationId == installation && u.IsActive && u.ArchivedAt == null, token);
                if (owner is null) continue;
                var column = await db.WorkBoardColumns.Where(c => c.BoardId == item.BoardId &&
                    c.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.ToDo).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(token);
                if (column is null) continue;
                item.Status = WorkTaskStatus.Ready; item.BoardColumnId = column;
                item.NextReviewAt = null; item.WaitingReason = null; item.WaitingOnOrganizationUserId = null;
                item.WaitingDependencyJson = null; item.Revision++; item.UpdatedAt = clock.GetUtcNow();
                await QueueAvailableAsync(item.OrganizationId, owner, item.BoardId!.Value, item.Id, item.UpdatedAt, token,
                    dependency.Kind == Wire.PersonalTodoDependencyKinds.ResourceChange ? null : item.Revision);
            }
            if (lastPage) break;
        }
    }
}
