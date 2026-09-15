using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkItemCollaborationService
{
    Task<WorkItemCollaborationResponse?> GetAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkItemCommentResponse?> AddCommentAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        AddWorkItemCommentRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkItemCommentResponse?> UpdateCommentAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid commentId,
        Guid applicationUserId,
        UpdateWorkItemCommentRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkItemCommentResponse?> DeleteCommentAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid commentId,
        Guid applicationUserId,
        DeleteWorkItemCommentRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkBoardItemResponse?> TransferAsync(
        Guid organizationId,
        Guid sourceBoardId,
        Guid itemId,
        Guid applicationUserId,
        TransferWorkItemRequest request,
        CancellationToken cancellationToken = default);
}
