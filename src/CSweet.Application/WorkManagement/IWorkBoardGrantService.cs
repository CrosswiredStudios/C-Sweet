using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkBoardGrantService
{
    Task<IReadOnlyList<WorkBoardGrantResponse>> ListOrganizationAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkBoardGrantResponse>> SetOrganizationSubjectGrantsAsync(
        Guid organizationId,
        Guid applicationUserId,
        SetWorkBoardSubjectGrantsRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkBoardGrantResponse>> ListAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkBoardGrantResponse>> SetSubjectGrantsAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        SetWorkBoardSubjectGrantsRequest request,
        CancellationToken cancellationToken = default);
}
