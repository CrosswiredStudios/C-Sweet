using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.BusinessOnboarding;

namespace CSweet.UI.Services;

public sealed class BusinessOnboardingApiClient : IBusinessOnboardingApiClient
{
    private readonly HttpClient _httpClient;

    public BusinessOnboardingApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CompleteBusinessOnboardingResponse> CompleteAsync(
        CompleteBusinessOnboardingRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/business-onboarding/complete", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return Deserialize<CompleteBusinessOnboardingResponse>(body)
                ?? throw new ApiClientException(response.StatusCode, "Business onboarding returned an invalid response.");
        }

        var error = Deserialize<BusinessOnboardingActionResponse>(body);
        throw new ApiClientException(
            response.StatusCode,
            error?.Message ?? ServerError(response.StatusCode, "Business onboarding failed."));
    }

    public async Task<CompleteChiefSetupResponse> AssignChiefAsync(
        Guid organizationId,
        CompleteChiefSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/business-onboarding/{organizationId}/chief", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
            return Deserialize<CompleteChiefSetupResponse>(body)
                ?? throw new ApiClientException(response.StatusCode, "Chief setup returned an invalid response.");
        var error = Deserialize<ChiefSetupActionResponse>(body);
        throw new ApiClientException(
            response.StatusCode,
            error?.Message ?? ServerError(response.StatusCode, "Chief setup failed."));
    }

    private static T? Deserialize<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string ServerError(System.Net.HttpStatusCode statusCode, string fallback) =>
        (int)statusCode >= 500
            ? "The C-Sweet server could not complete this operation. Your installed agent was preserved and it is safe to retry."
            : fallback;
}
