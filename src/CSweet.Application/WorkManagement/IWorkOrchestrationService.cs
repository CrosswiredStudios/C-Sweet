using CSweet.Contracts.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public interface IWorkOrchestrationService
{
    Task<WorkOrchestrationPolicyResponse?> GetPolicyAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkOrchestrationPolicyRevision> SavePolicyRevisionAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId,
        SaveWorkOrchestrationPolicyRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkOrchestrationPolicyRevision> PublishPolicyRevisionAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId,
        PublishWorkOrchestrationPolicyRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSprintPreflightResult> PreflightAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkSprintExecutionResponse> StartAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        WorkOrchestrationControlRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSprintExecutionResponse?> ControlAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        string action, WorkOrchestrationControlRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSprintExecutionResponse?> GetExecutionAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkStageExecutionResponse> RetryAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, Guid applicationUserId,
        WorkOrchestrationControlRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkStageExecutionResponse> CompleteManualAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, Guid applicationUserId,
        CompleteManualWorkStageRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkStageExecutionResponse> DecideApprovalAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, Guid applicationUserId,
        DecideWorkApprovalStageRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWorkOrchestrator
{
    Task PulseAsync(CancellationToken cancellationToken = default);
}
