using CSweet.Contracts.Auth;

namespace CSweet.Application.Auth;

public interface IAuthenticationService
{
    Task<AuthStatusResponse> GetStatusAsync(Guid? userId, string? antiforgeryToken = null, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RegisterAsync(RegisterAdminRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ResendConfirmationAsync(EmailRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ForgotPasswordAsync(EmailRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RecoverRootAsync(RecoverRootRequest request, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task LogoutAsync();
    Task<UserPreferencesResponse?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Persists already-validated preference values; null clears a preference.</summary>
    Task<UserPreferencesResponse?> UpdatePreferencesAsync(Guid userId, UpdateUserPreferencesRequest request, CancellationToken cancellationToken = default);
}
