using CSweet.Contracts.Analytics;

namespace CSweet.Application.Analytics;

public interface IInferenceAnalyticsService
{
    Task<InferenceAnalyticsResponse> GetAsync(
        Guid organizationId,
        InferenceAnalyticsWindow window,
        CancellationToken cancellationToken = default);
}
