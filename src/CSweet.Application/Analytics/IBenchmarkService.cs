using CSweet.Contracts.Analytics;

namespace CSweet.Application.Analytics;

public interface IBenchmarkService
{
    Task<BenchmarkCatalogResponse> GetAsync(CancellationToken ct = default);
    Task<BenchmarkDefinitionResponse> CreateDefinitionAsync(CreateBenchmarkDefinitionRequest request, Guid userId, CancellationToken ct = default);
    Task<BenchmarkCampaignResponse> LaunchAsync(LaunchBenchmarkRequest request, Guid userId, CancellationToken ct = default);
    Task<BenchmarkCampaignResponse?> GetCampaignAsync(Guid id, CancellationToken ct = default);
    Task CancelAsync(Guid trialId, CancellationToken ct = default);
    Task<BenchmarkAssessmentResponse> AssessAsync(Guid trialId, BenchmarkAssessmentRequest request, Guid userId, CancellationToken ct = default);
    Task<bool> AdvanceAsync(CancellationToken ct = default);
}
