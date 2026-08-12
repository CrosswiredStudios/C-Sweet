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

    public async Task<ExecutionCapacityOnboardingResponse> GetExecutionCapacityStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/setup/execution-capacity");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ExecutionCapacityOnboardingResponse>(cancellationToken)
            ?? throw new ApiClientException(response.StatusCode, "Execution-capacity response was empty.");
    }

    public async Task<ExecutionCapacityActionResponse> SelectExecutionModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            "api/setup/execution-capacity/mode",
            new SelectExecutionOnboardingModeRequest(mode), cancellationToken);
        return await ReadExecutionActionResponseAsync(response, cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> CreateExecutionEnrollmentAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            "api/setup/execution-capacity/enrollments", null, cancellationToken);
        return await ReadExecutionActionResponseAsync(response, cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> InstallLocalExecutionNodeAsync(
        string enrollmentToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/setup/execution-capacity/local-install",
            new InstallLocalExecutionNodeRequest(enrollmentToken), cancellationToken);
        return await ReadExecutionActionResponseAsync(response, cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> RevokeExecutionEnrollmentAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            $"api/setup/execution-capacity/enrollments/{enrollmentId:D}", cancellationToken);
        return await ReadExecutionActionResponseAsync(response, cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> ApproveExecutionNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/setup/execution-capacity/nodes/{nodeId:D}/approve", null, cancellationToken);
        return await ReadExecutionActionResponseAsync(response, cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> RejectExecutionNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/setup/execution-capacity/nodes/{nodeId:D}/reject", null, cancellationToken);
        return await ReadExecutionActionResponseAsync(response, cancellationToken);
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

    private static async Task<ExecutionCapacityActionResponse> ReadExecutionActionResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<ExecutionCapacityActionResponse>(cancellationToken);
        return result ?? throw new ApiClientException(response.StatusCode,
            $"Execution-capacity request failed with {(int)response.StatusCode}.");
    }
}
