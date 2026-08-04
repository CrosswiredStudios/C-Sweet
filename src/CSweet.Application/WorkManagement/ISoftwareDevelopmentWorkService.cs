using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface ISoftwareDevelopmentWorkService
{
    Task<IReadOnlyList<SourceControlRepositoryOptionResponse>> ListRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse> AssignAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        AssignSoftwareDevelopmentWorkItemRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse> UnassignAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        UnassignSoftwareDevelopmentWorkItemRequest request,
        CancellationToken cancellationToken = default);

}
