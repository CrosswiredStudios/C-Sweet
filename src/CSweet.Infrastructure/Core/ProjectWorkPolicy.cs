using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Authoritative project boundary shared by intake, planning and execution.</summary>
public sealed class ProjectWorkPolicy(CSweetDbContext db, TimeProvider clock)
{
    public async Task<bool> RequiresProjectAsync(Guid org, Guid installation, CancellationToken ct)
    {
        var manifest = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == installation && x.BusinessId == org.ToString("D") && x.IsEnabled)
            .Select(x => x.PackageVersion!.ManifestJson).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(manifest)) return false;
        using var json = System.Text.Json.JsonDocument.Parse(manifest);
        if (!json.RootElement.TryGetProperty("rolePolicy", out var role) || !role.TryGetProperty("requiresProject", out var value)) return false;
        if (value.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new InvalidOperationException("project.invalid_policy: requiresProject must be a boolean in the installed manifest.");
        return value.GetBoolean();
    }
    public async Task RequireCapabilityWorkAsync(Guid org, Guid installation, string capability, System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        // Conversation and configuration can clarify a request before it has a delivery project.
        if (capability.StartsWith("agent.configuration.", StringComparison.Ordinal) || capability.EndsWith(".converse.v1", StringComparison.Ordinal) ||
            !await RequiresProjectAsync(org, installation, ct)) return;
        var employee = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null)
            .Select(x => x.Id).SingleAsync(ct);
        Guid? Id(string name) => payload.ValueKind == System.Text.Json.JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;
        if ((Id("itemId") ?? Id("workItemId")) is { } itemId)
        {
            var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == itemId && x.ArchivedAt == null, ct)
                ?? throw new InvalidOperationException("project.work_not_found: The assigned delivery ticket is unavailable.");
            if (item.AssignedEmployeeId != employee || item.AssignedAgentInstallationId != installation)
                throw new UnauthorizedAccessException("project.assignment_required: This delivery ticket is not assigned to the requesting agent.");
            if (await db.LegacyDevelopmentAuthorizations.AnyAsync(x => x.OrganizationId == org && x.WorkItemId == item.Id, ct)) return;
            if (item.BoardId.HasValue) { await RequireAsync(org, employee, item.BoardId.Value, ct); return; }
        }
        if (Id("boardId") is { } board) { await RequireAsync(org, employee, board, ct); return; }
        throw new InvalidOperationException("project.required: Delivery requires an assigned project and its delivery ticket. Retain the request through project intake before dispatching work.");
    }

    public async Task RequireIfConfiguredAsync(WorkTask item, CancellationToken ct)
    {
        if (item.AssignedAgentInstallationId is { } agent && await RequiresProjectAsync(item.OrganizationId, agent, ct) &&
            (item.DevelopmentBriefJson != null || item.DeliverySpecificationJson != null || item.PlanningSpecificationJson != null || item.Description.Contains("csweet-direct-development-v1", StringComparison.Ordinal) ||
             await db.WorkBoards.AnyAsync(x => x.Id == item.BoardId && x.Kind == WorkBoardKind.Standard, ct)))
            await RequireWorkAsync(item, ct);
    }

    public async Task RequireAsync(Guid org, Guid employee, Guid boardId, CancellationToken ct)
    {
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == boardId && x.OrganizationId == org, ct);
        if (board?.WorkstreamId is not { } project || board.TeamId is not { } team || board.ArchivedAt is not null)
            throw new InvalidOperationException("project.required: Create or select a project and assign the developer before starting development.");
        var now = clock.GetUtcNow();
        var active = await db.Workstreams.AnyAsync(x => x.Id == project && x.OrganizationId == org &&
            (x.Status == WorkstreamStatus.Active || x.Status == WorkstreamStatus.Approved), ct);
        var member = await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == org && x.WorkstreamId == project && x.OrganizationUserId == employee && x.RemovedAt == null, ct);
        var teamMatches = await db.OrganizationTeams.AnyAsync(x => x.Id == team && x.OrganizationId == org && x.ArchivedAt == null, ct) &&
            await db.WorkstreamTeamAssignments.AnyAsync(x => x.WorkstreamId == project && x.OrganizationId == org && x.TeamId == team && x.EndsAt == null, ct) &&
            await db.TeamMemberships.AnyAsync(x => x.OrganizationId == org && x.OrganizationUserId == employee && x.TeamId == team && x.EndedAt == null, ct);
        var user = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == employee && x.IsActive && x.ArchivedAt == null, ct);
        if (!active || !member || !teamMatches || user is null)
            throw new InvalidOperationException("project.assignment_required: The project must be active and the developer must be an explicit participant on its team. Ask the project manager to update membership.");
        var subject = user.AgentInstallationId ?? user.Id;
        var kind = user.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
        if (!await db.ScopedActionGrants.AnyAsync(x => x.OrganizationId == org && x.SubjectId == subject && x.SubjectKind == kind &&
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId && x.Action == CSweet.Contracts.WorkManagement.WorkItemActions.Read && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now), ct))
            throw new UnauthorizedAccessException("project.grant_required: Project board access has been revoked. Ask the project manager to restore access.");
    }

    public async Task RequireWorkAsync(WorkTask item, CancellationToken ct)
    {
        // Only the rollout snapshot and continuation of a captured, already-started plan carry this exception.
        if (await db.LegacyDevelopmentAuthorizations.AnyAsync(x => x.OrganizationId == item.OrganizationId && x.WorkItemId == item.Id, ct)) return;
        if (item.AssignedEmployeeId is not { } employee || item.BoardId is not { } board)
            throw new InvalidOperationException("project.required: Development requires a project board and an assigned developer.");
        await RequireAsync(item.OrganizationId, employee, board, ct);
    }

    public async Task LockAsync(Guid org, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(org.ToByteArray(), 0)})", ct);
    }
}
