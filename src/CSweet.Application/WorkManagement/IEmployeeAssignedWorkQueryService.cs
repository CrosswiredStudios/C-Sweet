using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IEmployeeAssignedWorkQueryService
{
    Task<EmployeeAssignedWorkResponse> GetAsync(Guid organizationId, Guid employeeId,
        Guid viewerOrganizationUserId, CancellationToken cancellationToken = default);
}
