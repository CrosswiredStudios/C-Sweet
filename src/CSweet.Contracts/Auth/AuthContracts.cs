using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Auth;

public sealed record AuthStatusResponse(
    bool RegistrationOpen,
    bool IsAuthenticated,
    string? Email,
    bool IsEmailConfirmed,
    bool EmailRecoveryAvailable,
    string? AntiforgeryToken = null,
    string? PreferredLanguage = null,
    string? TimeZoneId = null);

/// <summary>
/// Choices shown before the first administrator exists. Read from local configuration only:
/// producing it never contacts C-Sweet services.
/// </summary>
public sealed record RegistrationOptionsResponse(bool RegistrationOpen, bool HostedSetupAvailable);

/// <summary>Hosted first run: name shown on the C-Sweet approval page and to the phone app.</summary>
public sealed record StartHostedSetupRequest([property: Required, MaxLength(80)] string InstallationName);

/// <summary>
/// State of a hosted first-run link. <c>Status</c> is waiting, approved, denied, expired, failed, missing or unavailable.
/// </summary>
public sealed record HostedSetupStatusResponse(
    string Status,
    string? UserCode = null,
    string? VerificationUri = null,
    string? CreateAccountUri = null,
    DateTimeOffset? ExpiresAt = null,
    int PollSeconds = 5,
    string? InstallationName = null,
    string? Email = null,
    string? Message = null);

/// <summary>Finishes hosted setup. The administrator email comes from the approving C-Sweet account, not the client.</summary>
public sealed record CompleteHostedSetupRequest(
    [property: Required, MaxLength(160)] string DisplayName,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword,
    [property: MaxLength(16)] string? PreferredLanguage = null,
    [property: MaxLength(64)] string? TimeZoneId = null);

/// <summary>Per-user presentation preferences. Null values mean "follow the device".</summary>
public sealed record UserPreferencesResponse(string? PreferredLanguage, string? TimeZoneId);

public sealed record UpdateUserPreferencesRequest(
    [property: MaxLength(16)] string? PreferredLanguage,
    [property: MaxLength(64)] string? TimeZoneId);

public sealed record RegisterAdminRequest(
    [property: Required, MaxLength(160)] string DisplayName,
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    [property: Required] string ConfirmPassword,
    // Detected by the client when the account is created and saved once as the account's preference.
    [property: MaxLength(16)] string? PreferredLanguage = null,
    [property: MaxLength(64)] string? TimeZoneId = null);

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
