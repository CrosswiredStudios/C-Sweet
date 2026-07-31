using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Auth;

public sealed record AuthStatusResponse(
    bool RegistrationOpen,
    bool IsAuthenticated,
    string? Email,
    bool IsEmailConfirmed,
    bool EmailRecoveryAvailable,
    string? AntiforgeryToken = null);

public sealed record RegisterAdminRequest(
    [property: Required, MaxLength(160)] string DisplayName,
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    bool RememberMe = false);

public sealed record ConfirmEmailRequest(Guid UserId, [property: Required] string Code);

public sealed record EmailRequest([property: Required, EmailAddress] string Email);

public sealed record ResetPasswordRequest(
    Guid UserId,
    [property: Required] string Code,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword);

public sealed record RecoverRootRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string RecoveryCode,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword);

public sealed record AuthActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    AuthStatusResponse? Session = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    IReadOnlyList<string>? RecoveryCodes = null);
