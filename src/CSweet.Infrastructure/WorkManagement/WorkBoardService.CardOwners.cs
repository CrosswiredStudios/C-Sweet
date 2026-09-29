using CSweet.Contracts.WorkManagement;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkBoardService
{
    internal async Task<List<WorkBoardItemResponse>> ResolveCardOwnersAsync(
        Guid organizationId, Guid boardId, IReadOnlyList<WorkBoardItemResponse> items, CancellationToken token)
    {
        var ids = items.Select(x => x.Id).ToList();
        var executions = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages)
            .Where(x => ids.Contains(x.WorkItemId) && x.SprintExecution!.OrganizationId == organizationId &&
                x.SprintExecution.BoardId == boardId &&
                (x.SprintExecution.Status == WorkSprintExecutionStatus.Active ||
                 x.SprintExecution.Status == WorkSprintExecutionStatus.Paused))
            .ToListAsync(token);
        var stages = executions.ToDictionary(x => x.WorkItemId,
            x => x.Stages.Where(s => s.StageKey == x.CurrentStageKey).OrderByDescending(s => s.CreatedAt).FirstOrDefault());
        var employeeIds = items.Select(x => x.AssignedEmployeeId)
            .Concat(stages.Values.Select(x => x?.OrganizationUserId)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var installationIds = items.Select(x => x.AssignedInstallationId)
            .Concat(stages.Values.Select(x => x?.AgentInstallationId)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var workerIds = items.Select(x => x.AssignedWorkerId).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var members = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            (employeeIds.Contains(x.Id) || (x.AgentInstallationId.HasValue && installationIds.Contains(x.AgentInstallationId.Value)) ||
             (x.WorkerId.HasValue && workerIds.Contains(x.WorkerId.Value)))).ToListAsync(token);
        return items.Select(item =>
        {
            stages.TryGetValue(item.Id, out var stage);
            var employeeId = stage is null ? item.AssignedEmployeeId : stage.OrganizationUserId;
            var installationId = stage is null ? item.AssignedInstallationId : stage.AgentInstallationId;
            var workerId = stage is null ? item.AssignedWorkerId : null;
            var member = members.FirstOrDefault(x =>
                employeeId.HasValue ? x.Id == employeeId :
                installationId.HasValue ? x.AgentInstallationId == installationId :
                workerId.HasValue && x.WorkerId == workerId);
            return item with
            {
                AssignedEmployeeId = member?.Id ?? employeeId,
                AssignedInstallationId = installationId ?? member?.AgentInstallationId,
                AssignedWorkerId = workerId ?? member?.WorkerId,
                AssignedDisplayName = member?.DisplayName ??
                    (stage?.PrincipalKind == WorkOrchestrationPrincipalKind.PlatformAction ? "Platform" :
                     stage is null ? item.AssignedDisplayName : null)
            };
        }).ToList();
    }
}
