using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface ISoftwareDevelopmentWorkService
{
    Task<IReadOnlyList<GitRepositoryConnectionResponse>> ListConnectionsAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<GitRepositoryConnectionResponse> CreateConnectionAsync(
        Guid organizationId,
        Guid applicationUserId,
        CreateGitRepositoryConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task GrantConnectionAsync(
        Guid organizationId,
        Guid connectionId,
        Guid applicationUserId,
        GrantGitRepositoryConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task SetCredentialAsync(
        Guid organizationId,
        Guid connectionId,
        Guid applicationUserId,
        SetGitRepositoryCredentialRequest request,
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
