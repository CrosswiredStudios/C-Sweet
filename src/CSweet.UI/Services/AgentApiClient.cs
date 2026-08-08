using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Agents;

namespace CSweet.UI.Services;

public sealed class AgentApiClient : IAgentApiClient
{
    private readonly HttpClient _httpClient;

    public AgentApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AgentImportPreviewResponse> PreviewImportAsync(
        PreviewAgentImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/agents/imports/preview",
            request,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<AgentImportPreviewResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Agent import preview response was empty.");
        }

        var error = await response.Content.ReadFromJsonAsync<AgentApiErrorResponse>(cancellationToken);
        throw new ApiClientException(response.StatusCode, error?.Error ?? "Agent import could not be previewed.");
    }

    public Task<AgentInstallationResponse> InstallAsync(
        Guid importId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Post,
            $"api/agents/imports/{importId}/install",
            request,
            cancellationToken);

    public async Task<IReadOnlyList<AgentInstallationResponse>> ListInstallationsAsync(
        CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<AgentInstallationResponse>>(
            "api/agents/installations",
            cancellationToken) ?? [];

    public Task<AgentInstallationResponse?> GetInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        _httpClient.GetFromJsonAsync<AgentInstallationResponse>(
            $"api/agents/installations/{installationId}",
            cancellationToken);

    public async Task<IReadOnlyList<AgentUpdateAvailabilityResponse>> CheckUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<IReadOnlyList<AgentUpdateAvailabilityResponse>>(
            HttpMethod.Post,
            "api/agents/installations/check-updates",
            null,
            cancellationToken);

    public Task<AgentInstallationResponse> UpdateAsync(
        Guid installationId,
        UpdateAgentInstallationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Post,
            $"api/agents/installations/{installationId}/update",
            request,
            cancellationToken);

    public Task<AgentInstallationResponse> UpdateScheduleAsync(
        Guid installationId,
        UpdateAgentScheduleRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Put,
            $"api/agents/installations/{installationId}/schedule",
            request,
            cancellationToken);

    public Task<AgentInstallationResponse> RunNowAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Post,
            $"api/agents/installations/{installationId}/run-now",
            null,
            cancellationToken);

    public Task<AgentInstallationResponse> RetryBuildAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Post,
            $"api/agents/installations/{installationId}/retry-build",
            null,
            cancellationToken);

    public Task<AgentInstallationResponse> RetryStartupAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Post,
            $"api/agents/installations/{installationId}/retry-startup",
            null,
            cancellationToken);

    public Task<AgentInstallationResponse> DisableAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(
            HttpMethod.Post,
            $"api/agents/installations/{installationId}/disable",
            null,
            cancellationToken);

    public Task<AgentInstallationResponse> EnableAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentInstallationResponse>(HttpMethod.Post, $"api/agents/installations/{installationId}/enable", null, cancellationToken);

    public Task<RemoveAgentInstallationResponse> RemoveAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<RemoveAgentInstallationResponse>(
            HttpMethod.Delete,
            $"api/agents/installations/{installationId}",
            null,
            cancellationToken);

    public async Task<IReadOnlyList<AgentRuntimeRunResponse>> ListRunsAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<AgentRuntimeRunResponse>>(
            $"api/agents/installations/{installationId}/runs", cancellationToken) ?? [];

    public async Task<AgentBuildLogResponse> GetBuildLogAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/agents/installations/{installationId}/build-log", cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<AgentBuildLogResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Build log response was empty.");
        throw new ApiClientException(response.StatusCode, "No build log is available for this installation.");
    }

    public Task<AgentRuntimeReadinessResponse> EnsureRuntimeAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentRuntimeReadinessResponse>(
            HttpMethod.Post,
            $"api/agents/installations/{installationId}/runtime/ensure",
            null,
            cancellationToken);

    public Task<AgentRuntimeReadinessResponse> GetRuntimeStatusAsync(
        Guid installationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<AgentRuntimeReadinessResponse>(
            HttpMethod.Get,
            $"api/agents/installations/{installationId}/runtime/status",
            null,
            cancellationToken);

    public async Task<AgentConfigurationSchemaResponse> GetConfigurationAsync(
        string installationId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"api/agents/installations/{Uri.EscapeDataString(installationId)}/configuration",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            throw await RuntimeNotReadyExceptionAsync(response, cancellationToken);
        }

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<AgentConfigurationSchemaResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Agent configuration response was empty.");
        }

        var error = await response.Content.ReadFromJsonAsync<AgentApiErrorResponse>(cancellationToken);
        throw new ApiClientException(response.StatusCode, error?.Error ?? "Agent configuration could not be loaded.");
    }

    public async Task<AgentConfigurationUpdateResponse> UpdateConfigurationAsync(
        string installationId,
        UpdateAgentConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"api/agents/installations/{Uri.EscapeDataString(installationId)}/configuration",
            request,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            throw await RuntimeNotReadyExceptionAsync(response, cancellationToken);
        }

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<AgentConfigurationUpdateResponse>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Agent configuration update response was empty.");
        }

        var error = await response.Content.ReadFromJsonAsync<AgentApiErrorResponse>(cancellationToken);
        throw new ApiClientException(response.StatusCode, error?.Error ?? "Agent configuration could not be saved.");
    }

    private static async Task<ApiClientException> RuntimeNotReadyExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var readiness = await response.Content.ReadFromJsonAsync<AgentRuntimeReadinessResponse>(cancellationToken);
        var detail = !string.IsNullOrWhiteSpace(readiness?.Reason)
            ? readiness.Reason
            : readiness?.Stage is { Length: > 0 } stage
                ? $"Current stage: {stage}."
                : null;
        var message = "The agent runtime is still starting. Wait a moment and try again.";
        if (detail is not null)
        {
            message = $"{message} {detail}";
        }

        return new ApiClientException(response.StatusCode, message);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string uri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new ApiClientException(response.StatusCode, "Agent management response was empty.");
        }

        var error = await response.Content.ReadFromJsonAsync<AgentApiErrorResponse>(cancellationToken);
        throw new ApiClientException(response.StatusCode, error?.Error ?? "Agent management action failed.");
    }

    private sealed record AgentApiErrorResponse(string? Error);
}
