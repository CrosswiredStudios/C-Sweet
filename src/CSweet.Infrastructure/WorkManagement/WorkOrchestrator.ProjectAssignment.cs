using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    /// <summary>
    /// Checks, before dispatch, that each agent whose manifest requires a project may work on this board's
    /// project. A hire made for the board's team that was never enrolled (for example, hired before the
    /// project's board existed) is enrolled now. Anything still failing is returned as a blocker, so the
    /// stage is parked with a visible reason and the board manager is woken, instead of the policy error
    /// aborting every scheduler pass while the ticket silently stays Pending.
    /// Runs before any stage in this pass changes, so enrollment saves only its own rows.
    /// </summary>
    private async Task<Dictionary<Guid, string>> ProjectAssignmentBlockersAsync(
        WorkSprintExecution execution, IReadOnlyList<WorkStageExecution> candidates, CancellationToken cancellationToken)
    {
        var blockers = new Dictionary<Guid, string>();
        var org = execution.OrganizationId;
        var policy = new ProjectWorkPolicy(db, timeProvider);
        foreach (var stage in candidates)
        {
            if (stage.AgentInstallationId is not { } installationId) continue;
            var item = stage.ItemExecution!.WorkItem!;
            if (!await policy.RequiresProjectAsync(org, installationId, cancellationToken) ||
                await db.LegacyDevelopmentAuthorizations.AnyAsync(x => x.OrganizationId == org && x.WorkItemId == item.Id, cancellationToken))
                continue;
            var employee = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.OrganizationId == org && x.AgentInstallationId == installationId && x.IsActive)
                .Select(x => new { x.Id, x.DisplayName }).SingleOrDefaultAsync(cancellationToken);
            if (employee is null)
            {
                blockers[stage.Id] = $"The agent assigned to {item.Identifier} is no longer an active employee. Reassign the ticket or restore the employee, then retry.";
                continue;
            }
            var reason = await ProjectRefusalAsync(policy, org, employee.Id, execution.BoardId, cancellationToken);
            if (reason is null) continue;
            if (await EnrollHireAsync(policy, execution, employee.Id, cancellationToken))
            {
                reason = await ProjectRefusalAsync(policy, org, employee.Id, execution.BoardId, cancellationToken);
                if (reason is null)
                {
                    logger.LogInformation("Enrolled hired team member {EmployeeId} in the project of board {BoardId} before dispatching {Item}.",
                        employee.Id, execution.BoardId, item.Identifier);
                    continue;
                }
            }
            blockers[stage.Id] = $"{employee.DisplayName} can't start {item.Identifier}: {reason}";
        }
        return blockers;
    }

    private static async Task<string?> ProjectRefusalAsync(
        ProjectWorkPolicy policy, Guid org, Guid employeeId, Guid boardId, CancellationToken cancellationToken)
    {
        try { await policy.RequireAsync(org, employeeId, boardId, cancellationToken); return null; }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException) { return error.Message; }
    }

    private async Task<bool> EnrollHireAsync(
        ProjectWorkPolicy policy, WorkSprintExecution execution, Guid employeeId, CancellationToken cancellationToken)
    {
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(x =>
            x.Id == execution.BoardId && x.OrganizationId == execution.OrganizationId, cancellationToken);
        if (board.WorkstreamId is not { } projectId || board.TeamId is not { } teamId) return false;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await policy.LockAsync(execution.OrganizationId, cancellationToken);
        if (!await new ProjectSetupService(db, timeProvider, policy).EnrollHiredTeamMemberAsync(
                execution.OrganizationId, teamId, employeeId, projectId, cancellationToken))
            return false;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
