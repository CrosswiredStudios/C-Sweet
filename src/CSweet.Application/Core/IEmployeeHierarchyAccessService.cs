namespace CSweet.Application.Core;

public interface IEmployeeHierarchyAccessService
{
    Task<Guid?> ResolveOrganizationUserIdAsync(Guid organizationId, Guid applicationUserId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlySet<Guid>> GetSelfAndDescendantsAsync(Guid organizationId, Guid organizationUserId,
        CancellationToken cancellationToken = default);
    Task<bool> CanAccessSensitiveAsync(Guid organizationId, Guid actorOrganizationUserId,
        Guid employeeId, CancellationToken cancellationToken = default);
}
