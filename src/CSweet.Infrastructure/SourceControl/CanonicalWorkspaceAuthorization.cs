using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

internal sealed class CanonicalWorkspaceAuthorization(CSweet.Infrastructure.Persistence.CSweetDbContext db)
{
    internal async Task<bool> AuthorizeAsync(SourceControlWorkspace workspace,
        string action, string? expectedCommit, CancellationToken ct)
    {
        var work = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == workspace.WorkItemId && x.OrganizationId == workspace.OrganizationId &&
            x.AssignmentRevision == workspace.AssignmentRevision && x.ArchivedAt == null &&
            x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled, ct);
        if (work?.BoardId is not { } boardId) return false;
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == workspace.OrganizationId && x.AgentInstallationId == workspace.AgentInstallationId &&
            x.IsActive && x.ArchivedAt == null, ct);
        if (employee is null) return false;
        var stages = await (from stage in db.WorkStageExecutions.AsNoTracking()
            join item in db.WorkItemExecutions.AsNoTracking() on stage.ItemExecutionId equals item.Id
            join sprint in db.WorkSprintExecutions.AsNoTracking() on item.SprintExecutionId equals sprint.Id
            where sprint.OrganizationId == workspace.OrganizationId && sprint.BoardId == boardId &&
                sprint.Status == WorkSprintExecutionStatus.Active && item.WorkItemId == work.Id &&
                item.Status == WorkItemExecutionStatus.Running && stage.StageKey == item.CurrentStageKey &&
                stage.Traversal == item.Traversal && stage.Status == WorkStageExecutionStatus.Running &&
                stage.PrincipalKind == WorkOrchestrationPrincipalKind.AgentInstallation &&
                stage.AgentInstallationId == workspace.AgentInstallationId && stage.OrganizationUserId == employee.Id &&
                (stage.StageType == WorkOrchestrationStageType.AgentExecution || stage.StageType == WorkOrchestrationStageType.MemberExecution) &&
                stage.Attempts.Any(a => a.AgentWorkItemId != null &&
                    (a.Status == WorkExecutionAttemptStatus.Pending || a.Status == WorkExecutionAttemptStatus.Running))
            select new { stage.StageKey, SprintId = sprint.Id }).Take(2).ToListAsync(ct);
        if (stages.Count != 1) return false;
        using var delivery = JsonDocument.Parse(work.DeliverySpecificationJson ?? "{}");
        if (!delivery.RootElement.TryGetProperty("repositoryId", out var repository) ||
            !repository.TryGetGuid(out var repositoryId) || repositoryId != workspace.RepositoryId) return false;
        if (!await db.WorkBoards.AnyAsync(x => x.Id == boardId && x.OrganizationId == workspace.OrganizationId &&
                x.TeamId == workspace.TeamId && x.ArchivedAt == null, ct)) return false;
        var now = DateTimeOffset.UtcNow;
        if (!await db.ScopedActionGrants.AnyAsync(x => x.OrganizationId == workspace.OrganizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == workspace.AgentInstallationId &&
            x.ScopeKind == GrantScopeKind.WorkItem && x.ScopeId == work.Id && x.Action == action &&
            x.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity && x.GrantedBySubjectId == stages[0].SprintId &&
            x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now), ct)) return false;
        if (stages[0].StageKey == "quality")
        {
            if (action is not (GitWorkspaceCapabilities.Prepare or GitWorkspaceCapabilities.Inspect or
                GitWorkspaceCapabilities.Sync or GitWorkspaceCapabilities.Cleanup) || string.IsNullOrWhiteSpace(expectedCommit)) return false;
            var commit = await (from publication in db.SourceControlPublications.AsNoTracking()
                join origin in db.SourceControlWorkspaces.AsNoTracking() on publication.WorkspaceId equals origin.Id
                where publication.OrganizationId == workspace.OrganizationId && origin.OrganizationId == workspace.OrganizationId &&
                    origin.WorkItemId == work.Id && origin.AssignmentRevision == workspace.AssignmentRevision &&
                    publication.RepositoryId == workspace.RepositoryId && publication.Status != SourceControlPublicationStatus.Superseded
                orderby publication.CreatedAt descending select publication.CommitSha).FirstOrDefaultAsync(ct);
            if (!string.Equals(commit, expectedCommit, StringComparison.OrdinalIgnoreCase)) return false;
        }
        await new ProjectWorkPolicy(db, TimeProvider.System).RequireAsync(workspace.OrganizationId, employee.Id, boardId, ct);
        return true;
    }

}
