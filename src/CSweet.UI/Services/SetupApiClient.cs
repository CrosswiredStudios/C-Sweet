using System.Net.Http.Json;
using System.Net.Http.Headers;
using CSweet.Contracts.Setup;

namespace CSweet.UI.Services;

public sealed class SetupApiClient : ISetupApiClient
{
    private readonly HttpClient _httpClient;

    public SetupApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<SetupStatusResponse>("api/setup/status", cancellationToken)
            ?? throw new ApiClientException(System.Net.HttpStatusCode.NoContent, "Setup status response was empty.");
    }

    public async Task<AgentIsolationOnboardingResponse> GetAgentIsolationStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/setup/agent-isolation");
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentIsolationOnboardingResponse>(cancellationToken)
            ?? throw new ApiClientException(System.Net.HttpStatusCode.NoContent,
                "Agent isolation status response was empty.");
    }

    public async Task<AgentIsolationOnboardingActionResponse> EnableHyperVAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync(
            "api/setup/agent-isolation/enable-hyperv", content: null, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AgentIsolationOnboardingActionResponse>(cancellationToken);
        return result ?? throw new ApiClientException(response.StatusCode,
            "Hyper-V enablement response was empty.");
    }

    public async Task<AgentIsolationOnboardingActionResponse> InstallRuntimeHostAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync(
            "api/setup/agent-isolation/install-runtime-host", content: null, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AgentIsolationOnboardingActionResponse>(cancellationToken);
        return result ?? throw new ApiClientException(response.StatusCode,
            "RuntimeHost installation response was empty.");
    }

    public async Task<SetupActionResponse> CompleteStepAsync(string key, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"api/setup/steps/{Uri.EscapeDataString(key)}/complete", content: null, cancellationToken);
        return await ReadActionResponseAsync(response, cancellationToken);
    }

    public async Task<SetupActionResponse> CompleteSetupAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/setup/complete", content: null, cancellationToken);
        return await ReadActionResponseAsync(response, cancellationToken);
    }

    private static async Task<SetupActionResponse> ReadActionResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<SetupActionResponse>(cancellationToken);
        if (result is not null)
        {
            return result;
        }

        throw new ApiClientException(response.StatusCode, $"Setup request failed with {(int)response.StatusCode}.");
    }
}
