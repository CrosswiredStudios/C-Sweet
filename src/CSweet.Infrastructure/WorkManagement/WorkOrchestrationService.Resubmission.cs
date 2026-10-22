using System.Data;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrationService
{
    private async Task<(OrganizationUser Member, WorkStageExecution Stage)> RequireResubmissionAsync(
        Guid organizationId, Guid boardId, Guid itemId, Guid userId, CancellationToken ct)
    {
        var member = await ResolveMemberAsync(organizationId, userId, ct);
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(entry => entry.Id == boardId && entry.OrganizationId == organizationId, ct);
        if (board.ManagerOrganizationUserId != member.Id && (member.EmployeeType != EmployeeType.Human ||
            !await HoldsBoardGrantAsync(organizationId, member, WorkOrchestrationActions.Retry, boardId, ct)))
            throw new UnauthorizedAccessException("Only the board manager or a person with the board retry grant may resubmit an integrated candidate.");
        var item = await db.WorkItemExecutions.Include(entry => entry.WorkItem).Include(entry => entry.SprintExecution)
            .Include(entry => entry.Stages).ThenInclude(entry => entry.Attempts)
            .Where(entry => entry.WorkItemId == itemId && entry.SprintExecution!.OrganizationId == organizationId &&
                entry.SprintExecution.BoardId == boardId && entry.SprintExecution.Status == WorkSprintExecutionStatus.Active)
            .OrderByDescending(entry => entry.UpdatedAt).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The ticket requires an active sprint.");
        var stage = WorkOrchestrationBoardState.CurrentStage(item);
        if (stage.StageKey is not ("specialist-execution" or "development") ||
            stage.Status is not (WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Failed) ||
            stage.Attempts.Any(attempt => attempt.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
            throw new InvalidOperationException("Only stopped author rework can resubmit an existing integrated candidate. No active work is interrupted.");
        return (member, stage);
    }

    public async Task<IntegratedCandidateResponse> GetIntegratedCandidateAsync(Guid organizationId, Guid boardId,
        Guid itemId, Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        var (_, stage) = await RequireResubmissionAsync(organizationId, boardId, itemId, applicationUserId, cancellationToken);
        return await IntegratedCandidateResubmission.RequireAsync(db, sourceControlHost, stage.ItemExecution!.WorkItem!, cancellationToken);
    }

    public async Task ResubmitIntegratedCandidateAsync(Guid organizationId, Guid boardId, Guid itemId,
        Guid applicationUserId, ResubmitIntegratedCandidateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 3500)
            throw new ArgumentException("Explain why unchanged rework should return to independent QA (up to 3500 characters).");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(entry => entry.Id == boardId && entry.OrganizationId == organizationId, cancellationToken);
        if (board.ManagerOrganizationUserId != member.Id && (member.EmployeeType != EmployeeType.Human ||
            !await HoldsBoardGrantAsync(organizationId, member, WorkOrchestrationActions.Retry, boardId, cancellationToken)))
            throw new UnauthorizedAccessException("Current board retry authority is required, including for replay.");
        var prior = await db.WorkOrchestrationEvents.AsNoTracking().SingleOrDefaultAsync(entry => entry.OrganizationId == organizationId &&
            entry.EventType == IntegratedCandidateResubmission.EventType && entry.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            using var data = JsonDocument.Parse(prior.DataJson);
            if (prior.BoardId != boardId || !await db.WorkItemExecutions.AnyAsync(entry => entry.Id == prior.ItemExecutionId && entry.WorkItemId == itemId, cancellationToken) ||
                data.RootElement.GetProperty("actorId").GetGuid() != member.Id || data.RootElement.GetProperty("integratedCommitSha").GetString() != request.ExpectedCommitSha ||
                data.RootElement.GetProperty("reason").GetString() != request.Reason || data.RootElement.GetProperty("itemRevision").GetInt64() != request.ExpectedRevision)
                throw new InvalidOperationException("The resubmission key is already bound to a different request.");
            await transaction.CommitAsync(cancellationToken); return;
        }
        var (_, stage) = await RequireResubmissionAsync(organizationId, boardId, itemId, applicationUserId, cancellationToken);
        var item = stage.ItemExecution!.WorkItem!;
        if (item.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The ticket changed. Refresh before resubmitting.");
        var candidate = await IntegratedCandidateResubmission.RequireAsync(db, sourceControlHost, item, cancellationToken);
        if (candidate.IntegratedCommitSha != request.ExpectedCommitSha)
            throw new DbUpdateConcurrencyException("The integrated candidate changed. Inspect the exact commit before resubmitting.");
        await IntegratedCandidateResubmission.QueueQaAsync(db, stage, candidate, member.Id, request.IdempotencyKey,
            request.Reason, timeProvider.GetUtcNow(), cancellationToken);
        await WorkOrchestrationBoardState.SaveBoardChangesAsync(db, stage.ItemExecution.SprintExecution!, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
