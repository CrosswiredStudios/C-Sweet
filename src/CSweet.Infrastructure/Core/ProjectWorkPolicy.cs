using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
        if (capability == CSweet.WorkManagement.Contracts.WorkManagementCapabilityNames.ExecutionRunV2)
        {
            var assignment = payload.Deserialize<CSweet.WorkManagement.Contracts.WorkExecutionAssignmentV2>(
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                ?? throw new UnauthorizedAccessException("project.assignment_required: The V2 assignment is missing.");
            if (assignment.Scope == CSweet.WorkManagement.Contracts.WorkExecutionScopes.Task)
                await RequireStageAssignmentAsync(org, installation, employee, System.Text.Json.JsonSerializer.SerializeToElement(
                    assignment.ToTaskAssignment(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), ct);
            else await RequireDeliveryAssignmentAsync(org, installation, employee, assignment, ct);
            return;
        }
        if (capability == CSweet.WorkManagement.Contracts.WorkManagementCapabilityNames.ExecutionRunV1)
        {
            await RequireStageAssignmentAsync(org, installation, employee, payload, ct);
            return;
        }
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

    private async Task RequireDeliveryAssignmentAsync(Guid org, Guid installation, Guid employee,
        CSweet.WorkManagement.Contracts.WorkExecutionAssignmentV2 assignment, CancellationToken ct)
    {
        var stage = await db.WorkStageExecutions.AsNoTracking().Include(x => x.Attempts)
            .Include(x => x.DeliveryExecution)!.ThenInclude(x => x!.Plan)
            .SingleOrDefaultAsync(x => x.Id == assignment.StageExecutionId, ct);
        var execution = stage?.DeliveryExecution;
        var plan = execution?.Plan;
        var newDispatch = stage?.Status == WorkStageExecutionStatus.Pending && execution?.Status == "Ready" &&
            assignment.Attempt == stage.Attempts.Count + 1 && !stage.Attempts.Any(x => x.Id == assignment.AttemptId);
        var queued = stage is not null && execution?.Status == "Running" &&
            stage.Status is WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running &&
            stage.Attempts.Any(x => x.Id == assignment.AttemptId && x.Attempt == assignment.Attempt &&
                x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running);
        if (plan is null || execution is null || stage is null || plan.Status != "Active" || plan.OrganizationId != org ||
            assignment.OrganizationId != org || assignment.WorkstreamId != plan.WorkstreamId || assignment.DeliveryPlanId != plan.Id ||
            assignment.ExecutionId != execution.Id || assignment.Scope != execution.Scope || assignment.ScopeRevision != plan.ScopeRevision ||
            execution.ScopeRevision != plan.ScopeRevision || assignment.BoardId != execution.BoardId || assignment.ItemId != execution.WorkItemId ||
            assignment.SprintId.HasValue || assignment.SprintExecutionId.HasValue || assignment.StageKey != execution.CurrentStageKey ||
            assignment.StageKey != stage.StageKey || assignment.Traversal != stage.Traversal || assignment.AttemptId == Guid.Empty ||
            assignment.Deadline <= clock.GetUtcNow() || stage.AgentInstallationId != installation || stage.OrganizationUserId != employee ||
            assignment.OrganizationUserId != employee || assignment.AgentInstallationId != installation || (!newDispatch && !queued) ||
            execution.CandidateJson is null || assignment.Candidate?.Digest != System.Text.Json.JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.WorkDeliveryCandidate>(
                execution.CandidateJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Digest)
            throw new UnauthorizedAccessException("project.assignment_required: The active delivery review is not assigned to this agent and candidate.");
        await RequireAsync(org, employee, execution.BoardId, ct);
    }

    private async Task RequireStageAssignmentAsync(Guid org, Guid installation, Guid employee,
        System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        CSweet.WorkManagement.Contracts.WorkExecutionAssignmentV1? assignment;
        try
        {
            assignment = System.Text.Json.JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.WorkExecutionAssignmentV1>(
                payload.GetRawText(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            throw new UnauthorizedAccessException("project.assignment_required: The stage assignment is invalid.");
        }
        var stage = assignment is null ? null : await db.WorkStageExecutions.AsNoTracking()
            .Include(x => x.Attempts).Include(x => x.ItemExecution)!.ThenInclude(x => x!.SprintExecution)
            .SingleOrDefaultAsync(x => x.Id == assignment.StageExecutionId, ct);
        var item = stage?.ItemExecution;
        var sprint = item?.SprintExecution;
        var newDispatch = assignment is not null && stage is not null && item is not null &&
            stage.Status == WorkStageExecutionStatus.Pending && item.Status == WorkItemExecutionStatus.Pending &&
            assignment.Attempt == stage.Attempts.Count + 1 && !stage.Attempts.Any(x => x.Id == assignment.AttemptId);
        var queuedDispatch = assignment is not null && stage is not null && item is not null &&
            (stage.Status == WorkStageExecutionStatus.Running && item.Status == WorkItemExecutionStatus.Running ||
             stage.Status == WorkStageExecutionStatus.Dispatching && item.Status == WorkItemExecutionStatus.Pending) &&
            stage.Attempts.Any(x => x.Id == assignment.AttemptId && x.Attempt == assignment.Attempt &&
                x.AgentWorkItemId.HasValue && x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running);
        if (assignment is null || stage is null || item is null || sprint is null ||
            assignment.OrganizationId != org || sprint.OrganizationId != org ||
            assignment.SprintExecutionId != sprint.Id || assignment.SprintId != sprint.SprintId ||
            assignment.BoardId != sprint.BoardId || assignment.PolicyRevisionId != sprint.PolicyRevisionId ||
            assignment.ItemExecutionId != item.Id || assignment.ItemId != item.WorkItemId ||
            assignment.StageKey != stage.StageKey || item.CurrentStageKey != stage.StageKey ||
            assignment.Traversal != stage.Traversal || item.Traversal != stage.Traversal ||
            assignment.AttemptId == Guid.Empty || (!newDispatch && !queuedDispatch) ||
            sprint.Status != WorkSprintExecutionStatus.Active || stage.AgentInstallationId != installation ||
            stage.OrganizationUserId != employee || stage.PrincipalKind != WorkOrchestrationPrincipalKind.AgentInstallation ||
            stage.StageType is not (WorkOrchestrationStageType.AgentExecution or WorkOrchestrationStageType.MemberExecution))
            throw new UnauthorizedAccessException("project.assignment_required: The active workflow stage is not assigned to the requesting agent.");
        if (!await db.CoreWorkTasks.AsNoTracking().AnyAsync(x => x.Id == item.WorkItemId &&
            x.OrganizationId == org && x.BoardId == sprint.BoardId && x.ArchivedAt == null &&
            x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled, ct))
            throw new UnauthorizedAccessException("project.assignment_required: The stage delivery ticket is unavailable.");
        // Canonical stage staffing owns orchestration work, including independent reviews.
        // A legacy ticket owner is not the execution principal for every stage.
        await RequireAsync(org, employee, sprint.BoardId, ct);
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
