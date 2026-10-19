using System.Net.Http.Json;
using CSweet.Contracts.Auth;

namespace CSweet.UI.Services;

public sealed class AuthenticationApiClient : IAuthenticationApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AuthSessionStore _session;

    public AuthenticationApiClient(HttpClient httpClient, AuthSessionStore session)
    {
        _httpClient = httpClient;
        _session = session;
    }

    public async Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await _httpClient.GetFromJsonAsync<AuthStatusResponse>("api/auth/session", cancellationToken)
            ?? throw new InvalidOperationException("Authentication status response was empty.");
        _session.Set(status);
        return status;
    }

    public Task<AuthActionResponse> RegisterAsync(RegisterAdminRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/register", request, cancellationToken);

    public Task<AuthActionResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/login", request, cancellationToken);

    public Task<AuthActionResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/confirm-email", request, cancellationToken);

    public Task<AuthActionResponse> ResendConfirmationAsync(string email, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/resend-confirmation", new EmailRequest(email), cancellationToken);

    public Task<AuthActionResponse> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/forgot-password", new EmailRequest(email), cancellationToken);

    public Task<AuthActionResponse> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/reset-password", request, cancellationToken);

    public Task<AuthActionResponse> RecoverRootAsync(RecoverRootRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/recover-root", request, cancellationToken);

    public Task<AuthActionResponse> RegenerateRecoveryCodesAsync(CancellationToken cancellationToken = default) =>
        PostAsync("api/auth/recovery-codes/regenerate", new { }, cancellationToken);

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/auth/logout", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        _session.Clear();
    }

    public async Task<UserPreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<UserPreferencesResponse>("api/auth/preferences", cancellationToken)
            ?? throw new InvalidOperationException("User preferences response was empty.");

    public async Task<UserPreferencesResponse> UpdatePreferencesAsync(UpdateUserPreferencesRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync("api/auth/preferences", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var preferences = await response.Content.ReadFromJsonAsync<UserPreferencesResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("User preferences response was empty.");
        if (_session.Current is { } current)
        {
            _session.Set(current with { PreferredLanguage = preferences.PreferredLanguage, TimeZoneId = preferences.TimeZoneId });
        }
        return preferences;
    }

    private async Task<AuthActionResponse> PostAsync<T>(string uri, T request, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(uri, request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AuthActionResponse>(cancellationToken: cancellationToken)
            ?? new AuthActionResponse(false, "empty_response", "The authentication service returned an empty response.");
        if (result.Session is not null)
        {
            _session.Set(result.Session);
            // Refresh to obtain an antiforgery token stored alongside the new cookie.
            await GetStatusAsync(cancellationToken);
        }
        return result;
    }
}
