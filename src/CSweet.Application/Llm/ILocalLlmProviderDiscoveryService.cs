using CSweet.Contracts.Llm;

namespace CSweet.Application.Llm;

public interface ILocalLlmProviderDiscoveryService
{
    Task<LocalLlmProviderDiscoveryResponse> DiscoverAsync(
        CancellationToken cancellationToken = default);
}
