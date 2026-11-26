using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Core;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class OverviewIssueReader
{
    private sealed record ComputeWait(Guid ProjectId, Guid InstallationId, Guid? BoardId, Guid? ItemId, bool Browser);

    private async Task<IReadOnlyList<OverviewIssueCard>> ReadComputeBlockersAsync(Guid org, Guid actor,
        IReadOnlyDictionary<Guid, string> projects, CancellationToken ct)
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
            select new ComputeWait(project.Id, stage.AgentInstallationId!.Value, board.Id, task.Id, true)).ToListAsync(ct);

        waits.AddRange(await (from stage in browserWaits
            join execution in db.WorkDeliveryExecutions on stage.DeliveryExecutionId equals (Guid?)execution.Id
            join plan in db.WorkDeliveryPlans on execution.PlanId equals plan.Id
            join project in activeProjects on plan.WorkstreamId equals project.Id
            where plan.OrganizationId == org && plan.Status == "Active" && execution.ScopeRevision == plan.ScopeRevision &&
                execution.Status == "WaitingForEvidence" && execution.CurrentStageKey == stage.StageKey &&
                !execution.Stages.Any(s => s.StageKey == stage.StageKey && s.Traversal > stage.Traversal) &&
                (execution.WorkItemId == null || tasks.Any(t => t.Id == execution.WorkItemId && t.BoardId == execution.BoardId))
            select new ComputeWait(project.Id, stage.AgentInstallationId!.Value, execution.BoardId, execution.WorkItemId, true)).ToListAsync(ct));

        waits.AddRange(await (from demand in db.Set<OfficeComputeCapacityRequest>().AsNoTracking()
            join project in activeProjects on demand.ProjectId equals project.Id
            where demand.OrganizationId == org
            select new ComputeWait(project.Id, demand.InstallationId, null, null, false)).ToListAsync(ct));
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
        var titles = await tasks.Where(t => itemIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);

        var legacy = preparation?.LegacyInteractive == true;
        var legacyTemplate = legacy ? await (from setup in db.Set<ComputeLocalSetup>().AsNoTracking()
            join template in db.ComputeTemplates.AsNoTracking() on setup.TemplateId equals template.TemplateId
            where setup.OrganizationId == org && setup.State == "Ready" && template.OrganizationId == org
            select template).SingleOrDefaultAsync(ct) : null;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var cards = new List<OverviewIssueCard>();
        bool? hasOfficeCapacity = null;
        foreach (var group in waits.GroupBy(w => (w.ProjectId, w.InstallationId)))
        {
            var (project, installation) = group.Key;
            if (!employees.TryGetValue(installation, out var employee)) continue;

            var visible = group.Where(w => w.BoardId is null || authorizedBoards.Contains(w.BoardId.Value)).ToArray();
            if (visible.Length == 0) continue;
            var browser = visible.Any(w => w.Browser);
            if (browser)
            {
                var template = legacy ? legacyTemplate :
                    await OfficeComputePlacement.FindProjectTemplateAsync(db, org, installation, project, now, ct);
                browser = template is null ||
                    JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, ComputeProtocol.Json) is not { } definition ||
                    !definition.Features.Contains("docker") || !definition.Features.Contains("browser-testing-v1");
            }
            if (!browser)
            {
                if (!visible.Any(w => !w.Browser)) continue;
                hasOfficeCapacity ??= await OfficeComputePlacement.FindAsync(db, org, now, ct) is not null;
                if (hasOfficeCapacity.Value) continue;
            }

            var title = browser ? "Waiting for a browser workspace" : "Waiting for an Office workspace";
            var summary = browser
                ? "I need a Linux workspace to run the browser tests. I’ll continue when one is available."
                : "I need an Office workspace to continue my work. I’ll continue when one is available.";
            var affected = visible.Where(w => browser && w.ItemId is Guid item && titles.ContainsKey(item))
                .Select(w => new OverviewAffectedWork(w.ItemId!.Value, titles[w.ItemId.Value],
                    $"/organizations/{org:D}/projects/{project:D}?tab=work&board={w.BoardId:D}&item={w.ItemId:D}"))
                .DistinctBy(w => w.ItemId).ToArray();
            cards.Add(new($"compute:{project:N}:{installation:N}", null, project, projects.GetValueOrDefault(project, "Project"),
                title, summary, employee.DisplayName, "", true, false, [], [], [], "")
            {
                OwnerOrganizationUserId = employee.EmployeeId,
                IsComputeBlocker = true,
                AffectedWork = affected,
                Actions = [new(installation, "Open Compute", $"/organizations/{org:D}/compute")]
            });
        }
        return cards;
    }
}
