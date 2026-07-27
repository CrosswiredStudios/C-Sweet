using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IOrganizationUserService
{
    Task<IReadOnlyList<OrganizationUserResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<OrganizationUserResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CoreActionResponse> CreateAsync(Guid organizationId, CreateOrganizationUserRequest request,
        CancellationToken cancellationToken = default, Guid? hiringApplicationUserId = null,
        string hiringSource = "Manual");
    Task<CoreActionResponse> UpdateRoleAsync(Guid organizationId, Guid id, UpdateOrganizationUserRoleRequest request, CancellationToken cancellationToken = default);
    Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
