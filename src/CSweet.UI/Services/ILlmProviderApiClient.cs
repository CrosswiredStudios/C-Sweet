using CSweet.Contracts.Llm;

namespace CSweet.UI.Services;

public interface ILlmProviderApiClient
{
    Task<IReadOnlyList<LlmProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task<LocalLlmProviderDiscoveryResponse> DiscoverLocalAsync(CancellationToken cancellationToken = default);
    Task<PreviewModelCatalogResponse> PreviewModelCatalogAsync(PreviewModelCatalogRequest request, CancellationToken cancellationToken = default);
    Task<PreviewModelCatalogResponse> GetModelCatalogAsync(Guid providerProfileId, CancellationToken cancellationToken = default);
    Task<LlmProviderProfileResponse> CreateAsync(CreateLlmProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task<LlmProviderProfileActionResponse> UpdateAsync(Guid providerProfileId, UpdateLlmProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task<LlmProviderProfileActionResponse> DeleteAsync(Guid providerProfileId, CancellationToken cancellationToken = default);
    Task<ModelCapabilityTestResult> TestAsync(Guid providerProfileId, CancellationToken cancellationToken = default);
    Task<LlmProviderProfileActionResponse> SetDefaultChatProviderAsync(Guid providerProfileId, CancellationToken cancellationToken = default);
    Task<LlmTokenUsageSummaryResponse> GetUsageSummaryAsync(CancellationToken cancellationToken = default);
}
