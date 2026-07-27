using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Llm;

namespace CSweet.UI.Services;

public sealed class LlmProviderApiClient : ILlmProviderApiClient
{
    private readonly HttpClient _httpClient;

    public LlmProviderApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<LlmProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<LlmProviderProfileResponse>>(
                "api/llm-provider-profiles",
                cancellationToken)
            ?? [];
    }

    public async Task<LocalLlmProviderDiscoveryResponse> DiscoverLocalAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync(
            "api/llm-provider-profiles/discover-local",
            content: null,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<LocalLlmProviderDiscoveryResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Local provider discovery response was empty.");
        }

        throw new ApiClientException(response.StatusCode, "Local provider discovery failed.");
    }

    public async Task<PreviewModelCatalogResponse> PreviewModelCatalogAsync(
        PreviewModelCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/llm-provider-profiles/model-catalog/preview",
            request,
            cancellationToken);

        return await response.Content.ReadFromJsonAsync<PreviewModelCatalogResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Model catalog preview response was empty.");
    }

    public async Task<PreviewModelCatalogResponse> GetModelCatalogAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"api/llm-provider-profiles/{providerProfileId}/models",
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<PreviewModelCatalogResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Model catalog response was empty.");
        }

        throw new ApiClientException(response.StatusCode, "Models could not be loaded for this provider.");
    }

    public async Task<LlmProviderProfileResponse> CreateAsync(
        CreateLlmProviderProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/llm-provider-profiles", request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<LlmProviderProfileResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Provider profile response was empty.");
        }

        var error = await response.Content.ReadFromJsonAsync<LlmProviderProfileActionResponse>(cancellationToken);
        throw new ApiClientException(response.StatusCode, error?.Message ?? $"Provider profile request failed with {(int)response.StatusCode}.");
    }

    public async Task<LlmProviderProfileActionResponse> UpdateAsync(
        Guid providerProfileId,
        UpdateLlmProviderProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"api/llm-provider-profiles/{providerProfileId}",
            request,
            cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<LlmProviderProfileActionResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Provider profile update response was empty.");

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiClientException(response.StatusCode, result.Message ?? $"Provider profile update failed with {(int)response.StatusCode}.");
        }

        return result;
    }

    public async Task<LlmProviderProfileActionResponse> DeleteAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.DeleteAsync(
            $"api/llm-provider-profiles/{providerProfileId}",
            cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<LlmProviderProfileActionResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Provider profile delete response was empty.");

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiClientException(response.StatusCode, result.Message ?? $"Provider profile delete failed with {(int)response.StatusCode}.");
        }

        return result;
    }

    public async Task<ModelCapabilityTestResult> TestAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"api/llm-provider-profiles/{providerProfileId}/test", content: null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModelCapabilityTestResult>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Provider test response was empty.");
    }

    public async Task<LlmProviderProfileActionResponse> SetDefaultChatProviderAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/setup/default-chat-provider",
            new SetDefaultChatProviderRequest(providerProfileId),
            cancellationToken);

        return await response.Content.ReadFromJsonAsync<LlmProviderProfileActionResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Default provider response was empty.");
    }

    public async Task<LlmTokenUsageSummaryResponse> GetUsageSummaryAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<LlmTokenUsageSummaryResponse>(
                "api/llm-provider-profiles/usage/summary",
                cancellationToken)
            ?? throw new ApiClientException(HttpStatusCode.OK, "Usage summary response was empty.");
    }
}
