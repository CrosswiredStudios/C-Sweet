using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface ITeamService
{
    Task<TeamDirectoryResponse> ListAsync(
        Guid organizationId,
        Guid applicationUserId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse?> GetAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse> CreateAsync(
        Guid organizationId,
        Guid applicationUserId,
        CreateTeamRequest request,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse> UpdateAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        UpdateTeamRequest request,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse> ArchiveAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        TeamRevisionRequest request,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse> RestoreAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        TeamRevisionRequest request,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse> UpsertMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        Guid organizationUserId,
        UpsertTeamMembershipRequest request,
        CancellationToken cancellationToken = default);

    Task<TeamDetailResponse> RemoveMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        Guid organizationUserId,
        TeamRevisionRequest request,
        CancellationToken cancellationToken = default);

    Task<Guid> ResolveApprovedTeamAsync(
        Guid organizationId,
        string teamKey,
        string name,
        string description,
        Guid leadOrganizationUserId,
        Guid sourceId,
        CancellationToken cancellationToken = default);

    Task AssignFromWorkflowAsync(
        Guid organizationId,
        Guid teamId,
        Guid organizationUserId,
        Guid? teamRoleId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken = default);
}
