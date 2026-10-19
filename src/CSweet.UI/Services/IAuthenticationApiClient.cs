using CSweet.Contracts.Auth;

namespace CSweet.UI.Services;

public interface IAuthenticationApiClient
{
    Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RegisterAsync(RegisterAdminRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ResendConfirmationAsync(string email, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RecoverRootAsync(RecoverRootRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RegenerateRecoveryCodesAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<UserPreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken = default);
    /// <summary>Saves presentation preferences and mirrors them into the cached session.</summary>
    Task<UserPreferencesResponse> UpdatePreferencesAsync(UpdateUserPreferencesRequest request, CancellationToken cancellationToken = default);
}
