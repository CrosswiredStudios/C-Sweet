using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class OverviewIssueReader
{
    private sealed record ComputeWait(Guid Id, Guid ProjectId, Guid InstallationId, Guid? BoardId, Guid? ItemId, bool Browser,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RetryAt, bool Waiting);

    private async Task<IReadOnlyList<OverviewIssueCard>> ReadComputeBlockersAsync(Guid org, Guid actor,
        IReadOnlyDictionary<Guid, string> projects, CancellationToken ct)
    {
        var requests = await ReadComputeRequestsAsync(db, issues, org, actor, projects, preparation?.LegacyInteractive == true,
            (clock ?? TimeProvider.System).GetUtcNow(), ct);
        return requests.Where(request => !request.CanRecheck && request.DiagnosticCode != null).Select(request =>
            new OverviewIssueCard($"compute:{request.ProjectId:N}:{request.EmployeeId:N}", null, request.ProjectId, request.Project,
                request.RequiresBrowser ? "Waiting for a browser workspace" : "Waiting for an Office workspace",
                request.RequiresBrowser
                    ? "I need a Linux workspace to run the browser tests. I’ll continue when one is available."
                    : "I need an Office workspace to continue my work. I’ll continue when one is available.",
                request.Employee, "", true, false, [], [], [], "")
            {
                IsComputeBlocker = true,
                OwnerOrganizationUserId = request.EmployeeId,
                AffectedWork = request.Work.Select(w => new OverviewAffectedWork(w.ItemId, w.Title, w.Uri)).ToArray(),
                Actions = [new(request.EmployeeId, "Open Compute", $"/organizations/{org:D}/compute?tab=requests")]
            }).ToArray();
    }

    public static async Task<IReadOnlyList<ComputeRequestStatus>> ReadComputeRequestsAsync(CSweetDbContext db,
        WorkIssueService issues, Guid org, Guid actor, IReadOnlyDictionary<Guid, string> projects,
        bool legacy, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(u => u.Id == actor && u.OrganizationId == org && u.IsActive &&
                u.ArchivedAt == null && u.EmployeeType == EmployeeType.Human && u.PermissionLevel == OrganizationPermissionLevel.Owner, ct))
            return [];

        var activeProjects = db.Workstreams.AsNoTracking().Where(p => p.OrganizationId == org &&
            p.Status != WorkstreamStatus.Completed && p.Status != WorkstreamStatus.Cancelled);
        var tasks = db.CoreWorkTasks.AsNoTracking().Where(t => t.OrganizationId == org && t.ArchivedAt == null &&
            t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled && t.Status != WorkTaskStatus.Failed);
        var boards = db.WorkBoards.AsNoTracking().Where(b => b.OrganizationId == org && b.ArchivedAt == null);
        var browserWaits = db.WorkStageExecutions.AsNoTracking().Where(s =>
            s.AgentInstallationId != null && s.LastOutcomeCode == WorkOrchestrator.ComputePreparationWait && s.LastError == null);

        var waits = await (from stage in browserWaits
            join execution in db.WorkItemExecutions on stage.ItemExecutionId equals (Guid?)execution.Id
            join sprint in db.WorkSprintExecutions on execution.SprintExecutionId equals sprint.Id
            join board in boards on sprint.BoardId equals board.Id
            join project in activeProjects on board.WorkstreamId equals (Guid?)project.Id
            join task in tasks on execution.WorkItemId equals task.Id
            where sprint.OrganizationId == org && sprint.Status == WorkSprintExecutionStatus.Active &&
                task.BoardId == board.Id && execution.CurrentStageKey == stage.StageKey && execution.Traversal == stage.Traversal &&
                (execution.Status == WorkItemExecutionStatus.Pending || execution.Status == WorkItemExecutionStatus.Running) &&
                (stage.Status == WorkStageExecutionStatus.Backoff || stage.Status == WorkStageExecutionStatus.Pending ||
                    stage.Status == WorkStageExecutionStatus.Dispatching || stage.Status == WorkStageExecutionStatus.Running)
            select new ComputeWait(stage.Id, project.Id, stage.AgentInstallationId!.Value, board.Id, task.Id, true,
                stage.CreatedAt, stage.UpdatedAt, stage.RetryAt, stage.Status == WorkStageExecutionStatus.Backoff)).ToListAsync(ct);

        waits.AddRange(await (from stage in browserWaits
            join execution in db.WorkDeliveryExecutions on stage.DeliveryExecutionId equals (Guid?)execution.Id
            join plan in db.WorkDeliveryPlans on execution.PlanId equals plan.Id
            join project in activeProjects on plan.WorkstreamId equals project.Id
            where plan.OrganizationId == org && plan.Status == "Active" && execution.ScopeRevision == plan.ScopeRevision &&
                execution.Status == "WaitingForEvidence" && execution.CurrentStageKey == stage.StageKey &&
                !execution.Stages.Any(s => s.StageKey == stage.StageKey && s.Traversal > stage.Traversal) &&
                (execution.WorkItemId == null || tasks.Any(t => t.Id == execution.WorkItemId && t.BoardId == execution.BoardId))
            select new ComputeWait(stage.Id, project.Id, stage.AgentInstallationId!.Value, execution.BoardId, execution.WorkItemId, true,
                stage.CreatedAt, stage.UpdatedAt, stage.RetryAt, true)).ToListAsync(ct));

        waits.AddRange(await (from demand in db.Set<OfficeComputeCapacityRequest>().AsNoTracking()
            join project in activeProjects on demand.ProjectId equals project.Id
            where demand.OrganizationId == org
            select new ComputeWait(demand.RequestId, project.Id, demand.InstallationId, null, null, false,
                demand.RequestedAt, demand.RequestedAt, null, true)).ToListAsync(ct));
        if (waits.Count == 0) return [];

        var businessId = org.ToString("D");
        var installationIds = waits.Select(w => w.InstallationId).Distinct().ToArray();
        var employees = await (from employee in db.CoreOrganizationUsers.AsNoTracking()
            join install in db.AgentInstallations on employee.AgentInstallationId equals (Guid?)install.Id
            where employee.OrganizationId == org && employee.EmployeeType == EmployeeType.Agent && employee.IsActive &&
                employee.ArchivedAt == null && install.BusinessId == businessId && install.IsEnabled &&
                install.RevisionStatus == PluginRevisionStatus.Active && install.SetupState == PluginSetupState.Ready &&
                installationIds.Contains(install.Id)
            select new { install.Id, EmployeeId = employee.Id, employee.DisplayName }).ToDictionaryAsync(e => e.Id, ct);

        var authorizedBoards = new HashSet<Guid>();
        foreach (var board in waits.Select(w => w.BoardId).OfType<Guid>().Distinct())
        {
            try { _ = await issues.RequireBoardAsync(org, actor, board, false, ct); authorizedBoards.Add(board); }
            catch (UnauthorizedAccessException) { }
        }
        var itemIds = waits.Where(w => w.BoardId is Guid board && authorizedBoards.Contains(board))
            .Select(w => w.ItemId).OfType<Guid>().ToArray();
        var work = await tasks.Where(t => itemIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.Identifier }).ToDictionaryAsync(t => t.Id, ct);

        var requests = new List<ComputeRequestStatus>();
        bool? hasOfficeCapacity = null;
        foreach (var group in waits.GroupBy(w => (w.ProjectId, w.InstallationId)))
        {
            var (project, installation) = group.Key;
            if (!employees.TryGetValue(installation, out var employee)) continue;

            var visible = group.Where(w => w.BoardId is null || authorizedBoards.Contains(w.BoardId.Value)).ToArray();
            if (visible.Length == 0) continue;
            var browserWaitsForProject = visible.Where(w => w.Browser).OrderBy(w => w.CreatedAt).ThenBy(w => w.Id).ToArray();
            if (browserWaitsForProject.Length > 0)
            {
                var browserError = await ComputeDefaultsService.BrowserReadinessErrorAsync(db, org, installation, project, now, legacy, ct);
                foreach (var wait in browserWaitsForProject)
                {
                    var affected = wait.ItemId is Guid item && work.TryGetValue(item, out var ticket)
                        ? new[] { new ComputeRequestWork(item, ticket.Title, ticket.Identifier,
                            $"/organizations/{org:D}/projects/{project:D}?tab=work&board={wait.BoardId:D}&item={item:D}") }
                        : [];
                    requests.Add(new(wait.Id, project, employee.EmployeeId, employee.DisplayName, projects.GetValueOrDefault(project, "Project"),
                        true, browserError == null && wait.Waiting && (wait.RetryAt == null || wait.RetryAt > now),
                        wait.CreatedAt, wait.UpdatedAt, wait.RetryAt,
                        browserError switch
                        {
                            null => "Browser compute is ready. The existing QA assignment can continue.",
                            OfficeComputeCapacityRequest.WaitingReason => "No eligible Office can provide a Linux workspace.",
                            "browser-testing-v1" => "The available Linux image does not support browser testing.",
                            _ => "Linux workspace preparation is incomplete."
                        }, browserError, affected));
                }
                continue;
            }
            var demand = visible.Single();
            hasOfficeCapacity ??= await OfficeComputePlacement.FindAsync(db, org, now, ct) is not null;
            if (hasOfficeCapacity.Value) continue;
            requests.Add(new(demand.Id, project, employee.EmployeeId, employee.DisplayName, projects.GetValueOrDefault(project, "Project"),
                false, false, demand.CreatedAt, demand.UpdatedAt, null, "Waiting for Office workspace capacity.",
                OfficeComputeCapacityRequest.WaitingReason, []));
        }
        return requests.OrderBy(r => r.RequestedAt).ThenBy(r => r.Id).ToArray();
    }
}
