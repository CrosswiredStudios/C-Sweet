using CSweet.Contracts.Analytics;

namespace CSweet.Application.Analytics;

public interface IWorkEfficiencyService
{
    Task<WorkEfficiencyResponse> GetAsync(Guid organizationId, DateTimeOffset? from = null,
        DateTimeOffset? to = null, CancellationToken cancellationToken = default);
    Task<EfficiencyActivityResponse> GetActivityAsync(Guid organizationId, Guid? workItemId,
        Guid? workstreamId, int offset, int limit, CancellationToken cancellationToken = default, bool? subtree = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null);
}
