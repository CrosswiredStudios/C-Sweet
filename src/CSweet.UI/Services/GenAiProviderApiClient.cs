using System.Net.Http.Json;
using CSweet.Contracts.GenAi;

namespace CSweet.UI.Services;

public interface IGenAiProviderApiClient
{
    Task<IReadOnlyList<GenAiProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task<LocalGenAiProviderDiscoveryResponse> DiscoverLocalAsync(CancellationToken cancellationToken = default);
    Task<GenAiConnectionTestResponse> TestDraftAsync(TestGenAiProviderConnectionRequest request, CancellationToken cancellationToken = default);
    Task<GenAiProviderProfileResponse> CreateAsync(CreateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task<GenAiProviderProfileResponse> UpdateAsync(Guid id, UpdateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GenAiConnectionTestResponse> TestAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GenAiOperationConfigurationResponse> SaveOperationAsync(Guid providerId, Guid? operationId, SaveGenAiOperationConfigurationRequest request, CancellationToken cancellationToken = default);
    Task SetDefaultAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed class GenAiProviderApiClient(HttpClient http) : IGenAiProviderApiClient
{
    public async Task<IReadOnlyList<GenAiProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<GenAiProviderProfileResponse>>("api/genai-provider-profiles", cancellationToken) ?? [];

    public async Task<LocalGenAiProviderDiscoveryResponse> DiscoverLocalAsync(CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsync("api/genai-provider-profiles/discover-local", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ApiClientException(response.StatusCode, "Local GenAI provider discovery failed.");
        return await response.Content.ReadFromJsonAsync<LocalGenAiProviderDiscoveryResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Local GenAI provider discovery response was empty.");
    }

    public async Task<GenAiConnectionTestResponse> TestDraftAsync(
        TestGenAiProviderConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync("api/genai-provider-profiles/test", request, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenAiConnectionTestResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Connection test response was empty.");
    }

    public async Task<GenAiProviderProfileResponse> CreateAsync(CreateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync("api/genai-provider-profiles", request, cancellationToken);
        return (await ReadAsync(response, cancellationToken)).Profile!;
    }

    public async Task<GenAiProviderProfileResponse> UpdateAsync(Guid id, UpdateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default)
    {
        var response = await http.PutAsJsonAsync($"api/genai-provider-profiles/{id}", request, cancellationToken);
        return (await ReadAsync(response, cancellationToken)).Profile!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await http.DeleteAsync($"api/genai-provider-profiles/{id}", cancellationToken);
        await ReadAsync(response, cancellationToken);
    }

    public async Task<GenAiConnectionTestResponse> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsync($"api/genai-provider-profiles/{id}/test", null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenAiConnectionTestResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Connection test response was empty.");
    }

    public async Task<GenAiOperationConfigurationResponse> SaveOperationAsync(Guid providerId, Guid? operationId, SaveGenAiOperationConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        var response = operationId.HasValue
            ? await http.PutAsJsonAsync($"api/genai-provider-profiles/{providerId}/operations/{operationId}", request, cancellationToken)
            : await http.PostAsJsonAsync($"api/genai-provider-profiles/{providerId}/operations", request, cancellationToken);
        return (await ReadAsync(response, cancellationToken)).Operation!;
    }

    public async Task SetDefaultAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync("api/genai-provider-profiles/defaults", new SetGenAiOperationDefaultRequest(operationId), cancellationToken);
        await ReadAsync(response, cancellationToken);
    }

    private static async Task<GenAiActionResponse> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        var result = await response.Content.ReadFromJsonAsync<GenAiActionResponse>(token)
            ?? throw new ApiClientException(response.StatusCode, "GenAI provider response was empty.");
        if (!response.IsSuccessStatusCode || !result.Succeeded)
            throw new ApiClientException(response.StatusCode, result.Message ?? "GenAI provider request failed.");
        return result;
    }
}
