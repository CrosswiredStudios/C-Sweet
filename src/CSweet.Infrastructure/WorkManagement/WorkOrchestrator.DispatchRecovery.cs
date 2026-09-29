using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    // Recover inbox rows written by the former non-transactional dispatcher without re-sending work.
    private async Task<bool> RecoverDetachedAttemptAsync(WorkSprintExecution execution, WorkStageExecution stage,
        Guid installationId, int attemptNumber, string key, CancellationToken token)
    {
        var work = await db.AgentWorkItems.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId && x.IdempotencyKey == key, token);
        if (work is null) return false;
        if (work.OrganizationId != execution.OrganizationId.ToString() || work.Kind != AgentWorkKind.Capability ||
            work.Name != Shared.WorkManagementCapabilityNames.ExecutionRunV1 ||
            work.SourceType != "WorkStageExecution" || work.SourceId != stage.Id.ToString("D"))
            throw new InvalidOperationException("Detached work does not match the authoritative execution stage.");
        var assignment = inbox.ReadPayload(work).Deserialize<Shared.WorkExecutionAssignmentV1>(JsonOptions)
            ?? throw new InvalidOperationException("Detached work has no assignment envelope.");
        if (assignment.OrganizationId != execution.OrganizationId || assignment.BoardId != execution.BoardId ||
            assignment.SprintId != execution.SprintId || assignment.SprintExecutionId != execution.Id ||
            assignment.ItemExecutionId != stage.ItemExecutionId || assignment.ItemId != stage.ItemExecution!.WorkItemId ||
            assignment.StageExecutionId != stage.Id || assignment.StageKey != stage.StageKey ||
            assignment.Traversal != stage.Traversal || assignment.Attempt != attemptNumber || assignment.AttemptId == Guid.Empty)
            throw new InvalidOperationException("Detached work has mismatched execution identity.");
        var attempt = new WorkExecutionAttempt
        {
            Id = assignment.AttemptId, StageExecutionId = stage.Id, AgentWorkItemId = work.Id,
            Attempt = attemptNumber, IdempotencyKey = key, Status = WorkExecutionAttemptStatus.Pending,
            CreatedAt = work.CreatedAt
        };
        stage.Attempts.Add(attempt);
        db.Entry(attempt).State = EntityState.Added;
        stage.Status = WorkStageExecutionStatus.Dispatching;
        stage.ItemExecution.Status = WorkItemExecutionStatus.Pending;
        stage.UpdatedAt = timeProvider.GetUtcNow();
        AddEvent(execution, stage.ItemExecutionId, stage.Id, attempt.Id,
            "attempt.dispatch.recovered", new { workId = work.Id, installationId });
        return true;
    }
}
