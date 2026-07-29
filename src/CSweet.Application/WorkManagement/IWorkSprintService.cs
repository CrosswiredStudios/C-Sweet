using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkSprintService
{
    Task<IReadOnlyList<WorkSprintResponse>> ListAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkSprintResponse> CreateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CreateWorkSprintRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSprintResponse?> ChangeStateAsync(
        Guid organizationId,
        Guid boardId,
        Guid sprintId,
        Guid applicationUserId,
        string action,
        ChangeWorkSprintStateRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse?> SetItemSprintAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        SetWorkItemSprintRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse?> SetItemEstimateAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        SetWorkItemEstimateRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSprintResponse?> SetCapacityAsync(
        Guid organizationId,
        Guid boardId,
        Guid sprintId,
        Guid applicationUserId,
        SetWorkSprintCapacityRequest request,
        CancellationToken cancellationToken = default);

    Task<SprintCarryoverResponse?> CarryOverAsync(
        Guid organizationId,
        Guid boardId,
        Guid sourceSprintId,
        Guid applicationUserId,
        CarryOverSprintRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSprintReportResponse> GetReportAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);
}
