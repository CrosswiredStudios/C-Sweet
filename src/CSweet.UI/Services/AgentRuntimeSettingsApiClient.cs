using CSweet.Contracts.Setup;
using System.Net.Http.Json;

namespace CSweet.UI.Services;

public sealed class AgentRuntimeSettingsApiClient : IAgentRuntimeSettingsApiClient
{
    private readonly HttpClient _httpClient;

    public AgentRuntimeSettingsApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AgentRuntimeSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<AgentRuntimeSettingsResponse>(
            "/api/agent-runtime/settings", cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize agent runtime settings.");
    }

    public async Task<AgentRuntimeSettingsActionResponse> UpdateAsync(
        UpdateAgentRuntimeSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/agent-runtime/settings", request, cancellationToken);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.BadRequest)
        {
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<AgentRuntimeSettingsActionResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize agent runtime settings action response.");

        return result;
    }

    public async Task<AgentRuntimeSettingsActionResponse> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync(
            "/api/agent-runtime/settings/recover",
            content: null,
            cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.BadRequest)
            response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentRuntimeSettingsActionResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize agent runtime recovery response.");
    }
}
