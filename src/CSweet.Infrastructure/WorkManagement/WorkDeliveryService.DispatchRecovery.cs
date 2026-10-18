using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    internal static WorkStageExecution CurrentDeliveryStage(WorkDeliveryExecution execution)
    {
        var matching = execution.Stages.Where(stage => stage.StageKey == execution.CurrentStageKey).ToArray();
        if (matching.Length == 0) throw new InvalidOperationException("The current delivery review stage is missing.");
        var traversal = matching.Max(stage => stage.Traversal);
        var current = matching.Where(stage => stage.Traversal == traversal).ToArray();
        return current.Length == 1 ? current[0] : throw new InvalidOperationException("The current delivery review stage is ambiguous.");
    }

    private async Task<bool> ResumePendingDeliveryDispatchAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution,
        WorkStageExecution stage, Guid installation, CancellationToken ct)
    {
        var pending = await inbox!.ReadPendingDeliveryDispatchAsync(plan.OrganizationId, installation, stage.Id, ct);
        if (pending is null) return false;
        var (work, payload) = pending.Value;
        // Reauthorize current scope, candidate, assignee and attempt through the same
        // policy used at admission. Historical queued payloads never grant execution.
        await new ProjectWorkPolicy(db, clock).RequireCapabilityWorkAsync(plan.OrganizationId, installation, work.Name, payload, ct);
        var assignment = payload.Deserialize<WorkExecutionAssignment>(Json)
            ?? throw new InvalidOperationException("The retained delivery dispatch has no assignment.");
        if (work.DeadlineAt <= clock.GetUtcNow() || assignment.Deadline.UtcTicks / 10 != work.DeadlineAt.UtcTicks / 10 ||
            assignment.OrganizationId != plan.OrganizationId || assignment.WorkstreamId != plan.WorkstreamId ||
            assignment.DeliveryPlanId != plan.Id || assignment.ExecutionId != execution.Id ||
            assignment.Scope != execution.Scope || assignment.ScopeRevision != plan.ScopeRevision ||
            assignment.BoardId != execution.BoardId || assignment.ItemId != execution.WorkItemId ||
            assignment.StageExecutionId != stage.Id || assignment.StageKey != execution.CurrentStageKey ||
            assignment.StageKey != stage.StageKey || assignment.Traversal != stage.Traversal ||
            assignment.AgentInstallationId != installation || assignment.OrganizationUserId != stage.OrganizationUserId ||
            assignment.SprintId is not null || assignment.SprintExecutionId is not null ||
            assignment.Candidate?.Digest != Candidate(execution)?.Digest || assignment.AttemptId == Guid.Empty ||
            stage.Status != WorkStageExecutionStatus.Pending || execution.Status != "Ready" ||
            stage.Attempts.Any(x => x.Id == assignment.AttemptId) || assignment.Attempt != stage.Attempts.Count + 1 ||
            work.IdempotencyKey != $"delivery:{stage.Id:N}:{assignment.Attempt}" || work.AttemptCount != 0)
            throw new InvalidOperationException("The retained delivery dispatch is stale or already attempted; inspect its evidence before recovery.");
        var attempt = new WorkExecutionAttempt { Id = assignment.AttemptId, StageExecutionId = stage.Id,
            AgentWorkItemId = work.Id, Attempt = assignment.Attempt, Status = WorkExecutionAttemptStatus.Pending,
            CreatedAt = work.CreatedAt, IdempotencyKey = work.IdempotencyKey };
        stage.Attempts.Add(attempt); db.WorkExecutionAttempts.Add(attempt);
        stage.Status = WorkStageExecutionStatus.Dispatching; execution.Status = "Running"; execution.Revision++;
        return true;
    }
}
