using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.Core;

public interface IEmployeeDetailsService
{
    Task<EmployeeDetailsResponse> GetAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<EmployeeDetailsResponse> UpdateProfileAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, UpdateEmployeeProfileRequest request,
        CancellationToken cancellationToken = default);
}
