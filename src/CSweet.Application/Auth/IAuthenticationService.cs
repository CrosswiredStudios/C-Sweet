using CSweet.Contracts.Auth;

namespace CSweet.Application.Auth;

public interface IAuthenticationService
{
    Task<AuthStatusResponse> GetStatusAsync(Guid? userId, string? antiforgeryToken = null, CancellationToken cancellationToken = default);
    Task<AuthActionResponse> RegisterAsync(RegisterAdminRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Hosted first run: creates the initial administrator with the email of the approving C-Sweet account, links that
    /// account as an external login and records the installation link, all in one transaction. The request's email is ignored.
    /// </summary>
    Task<AuthActionResponse> RegisterHostedAsync(RegisterAdminRequest request, HostedAdministratorLink link, CancellationToken cancellationToken = default);
    /// <summary>
    /// Links an existing installation (IL-11): checks the administrator's local password, adds the C-Sweet login,
    /// retires any previous link and records the new one with its owner directory publication, in one transaction.
    /// Local accounts, passwords and recovery codes are unchanged.
    /// </summary>
    Task<AuthActionResponse> LinkExistingAsync(Guid administratorId, string password, HostedAdministratorLink link, CancellationToken cancellationToken = default);
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
