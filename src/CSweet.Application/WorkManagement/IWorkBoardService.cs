using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkBoardService
{
    Task<WorkBoardDirectoryResponse> ListDirectoryAsync(
        Guid organizationId,
        Guid applicationUserId,
        WorkBoardDirectoryQuery query,
        CancellationToken cancellationToken = default);

    Task<WorkBoardDetailResponse?> GetAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkBoardDetailResponse> CreateAsync(
        Guid organizationId,
        Guid applicationUserId,
        CreateWorkBoardRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardDetailResponse?> UpdateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        UpdateWorkBoardRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ArchiveAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<bool> SetFavoriteAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    Task<WorkBoardDetailResponse?> ConfigureColumnsAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        ConfigureWorkBoardColumnsRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse> CreateItemAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CreateBoardWorkItemRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse?> MoveItemAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        MoveBoardWorkItemRequest request,
        CancellationToken cancellationToken = default);
}
