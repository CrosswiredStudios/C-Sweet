using System.Text;
using System.Security.Cryptography;
using CSweet.Application.Auth;
using CSweet.Application.Setup;
using CSweet.Contracts.Auth;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Auth;

public sealed class AuthenticationService : IAuthenticationService
{
    public const string AdministratorRole = "SystemAdministrator";
    private static readonly SemaphoreSlim RegistrationLock = new(1, 1);
    private static readonly SemaphoreSlim RecoveryLock = new(1, 1);
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan RememberedSessionLifetime = TimeSpan.FromDays(30);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole<Guid>> _roleManager;
    private readonly CSweetDbContext _dbContext;
    private readonly IAccountEmailSender _emailSender;
    private readonly IEmailDeliveryProfileService _emailDeliveryProfiles;
    private readonly IAuditEventWriter _auditWriter;

    public AuthenticationService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        CSweetDbContext dbContext,
        IAccountEmailSender emailSender,
        IEmailDeliveryProfileService emailDeliveryProfiles,
        IAuditEventWriter auditWriter)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        _emailSender = emailSender;
        _emailDeliveryProfiles = emailDeliveryProfiles;
        _auditWriter = auditWriter;
    }

    public async Task<AuthStatusResponse> GetStatusAsync(Guid? userId, string? antiforgeryToken = null, CancellationToken cancellationToken = default)
    {
        var registrationOpen = !await _dbContext.Users.AnyAsync(x => x.IsInitialAdministrator, cancellationToken);
        var user = userId.HasValue
            ? await _userManager.FindByIdAsync(userId.Value.ToString())
            : null;
        var emailRecoveryAvailable = await _emailDeliveryProfiles.HasReadyDefaultAsync(cancellationToken);
        return new AuthStatusResponse(
            registrationOpen,
            user is not null,
            user?.Email,
            user?.EmailConfirmed == true,
            emailRecoveryAvailable,
            antiforgeryToken);
    }

    public async Task<AuthActionResponse> RegisterAsync(RegisterAdminRequest request, CancellationToken cancellationToken = default)
    {
        await RegistrationLock.WaitAsync(cancellationToken);
        try
        {
            return await RegisterCoreAsync(request, cancellationToken);
        }
        finally
        {
            RegistrationLock.Release();
        }
    }

    private async Task<AuthActionResponse> RegisterCoreAsync(RegisterAdminRequest request, CancellationToken cancellationToken)
    {
        var displayName = request.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160)
        {
            return Validation("invalid_display_name", "Your name is required and must be 160 characters or fewer.", "DisplayName");
        }

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Validation("password_mismatch", "The password confirmation does not match.", "ConfirmPassword");
        }

        if (await _dbContext.Users.AnyAsync(x => x.IsInitialAdministrator, cancellationToken))
        {
            return Failure("registration_closed", "The initial administrator has already been registered.");
        }

        var email = request.Email.Trim();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            UserName = email,
            Email = email,
            IsInitialAdministrator = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        IdentityResult created;
        try
        {
            created = await _userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException)
        {
            return Failure("registration_closed", "The initial administrator has already been registered.");
        }

        if (!created.Succeeded)
        {
            return IdentityFailure(created);
        }

        if (!await _roleManager.RoleExistsAsync(AdministratorRole))
        {
            await _roleManager.CreateAsync(new IdentityRole<Guid>(AdministratorRole));
        }

        await _userManager.AddToRoleAsync(user, AdministratorRole);
        await ReconcileExistingOrganizationsAsync(user, cancellationToken);

        var recoveryCodes = await ReplaceRecoveryCodesAsync(user, cancellationToken);
        await _signInManager.SignInAsync(user, isPersistent: false);

        await _auditWriter.WriteAsync(
            "authentication.admin_registered",
            "ApplicationUser",
            user.Id,
            "The initial administrator account was registered.",
            cancellationToken: cancellationToken);

        return new AuthActionResponse(
            true,
            null,
            "Registration succeeded. Save your recovery codes before continuing.",
            await GetStatusAsync(user.Id, cancellationToken: cancellationToken),
            RecoveryCodes: recoveryCodes);
    }

    public async Task<AuthActionResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return Failure("invalid_credentials", "The email or password is incorrect.");
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return Failure("account_locked", "Too many failed sign-in attempts. Try again later.");
        }

        if (result.IsNotAllowed)
        {
            return Failure("email_not_confirmed", "Confirm your email before signing in.");
        }

        if (!result.Succeeded)
        {
            return Failure("invalid_credentials", "The email or password is incorrect.");
        }

        await _signInManager.SignInAsync(user, new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            IsPersistent = request.RememberMe,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(request.RememberMe ? RememberedSessionLifetime : SessionLifetime)
        });

        return Success("Signed in successfully.", await GetStatusAsync(user.Id, cancellationToken: cancellationToken));
    }

    public async Task<AuthActionResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || !TryDecodeToken(request.Code, out var token))
        {
            return Failure("invalid_confirmation", "The confirmation link is invalid or expired.");
        }

        var result = user.EmailConfirmed ? IdentityResult.Success : await _userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            return Failure("invalid_confirmation", "The confirmation link is invalid or expired.");
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        return Success("Email confirmed. You are now signed in.", await GetStatusAsync(user.Id, cancellationToken: cancellationToken));
    }

    public async Task<AuthActionResponse> ResendConfirmationAsync(EmailRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _emailDeliveryProfiles.HasReadyDefaultAsync(cancellationToken))
        {
            return Failure("mail_not_configured", "Email delivery is not configured.");
        }

        var user = await _userManager.FindByEmailAsync(request.Email.Trim());
        if (user is not null && !user.EmailConfirmed)
        {
            var token = EncodeToken(await _userManager.GenerateEmailConfirmationTokenAsync(user));
            try
            {
                await _emailSender.SendConfirmationAsync(user.Email!, user.Id, token, cancellationToken);
            }
            catch
            {
                return Failure("email_delivery_failed", "The confirmation email could not be delivered.");
            }
        }

        return Success("If an unconfirmed account exists for that email, a confirmation message has been sent.");
    }

    public async Task<AuthActionResponse> ForgotPasswordAsync(EmailRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _emailDeliveryProfiles.HasReadyDefaultAsync(cancellationToken))
        {
            return Failure("mail_not_configured", "Email delivery is not configured.");
        }

        var user = await _userManager.FindByEmailAsync(request.Email.Trim());
        if (user is not null && user.EmailConfirmed)
        {
            var token = EncodeToken(await _userManager.GeneratePasswordResetTokenAsync(user));
            try
            {
                await _emailSender.SendPasswordResetAsync(user.Email!, user.Id, token, cancellationToken);
            }
            catch
            {
                return Failure("email_delivery_failed", "The password reset email could not be delivered.");
            }
        }

        return Success("If a confirmed account exists for that email, a password reset message has been sent.");
    }

    public async Task<AuthActionResponse> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Validation("password_mismatch", "The password confirmation does not match.", "ConfirmPassword");
        }

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || !TryDecodeToken(request.Code, out var token))
        {
            return Failure("invalid_reset", "The password reset link is invalid or expired.");
        }

        var result = await _userManager.ResetPasswordAsync(user, token, request.Password);
        return result.Succeeded
            ? Success("Password reset successfully. You can now sign in.")
            : IdentityFailure(result, "invalid_reset");
    }

    public async Task<AuthActionResponse> RecoverRootAsync(RecoverRootRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Validation("password_mismatch", "The password confirmation does not match.", "ConfirmPassword");
        }

        await RecoveryLock.WaitAsync(cancellationToken);
        try
        {
            var user = await _userManager.FindByEmailAsync(request.Email.Trim());
            if (user is null || !user.IsInitialAdministrator)
            {
                return Failure("invalid_recovery", "The recovery details are invalid.");
            }

            foreach (var validator in _userManager.PasswordValidators)
            {
                var validation = await validator.ValidateAsync(_userManager, user, request.Password);
                if (!validation.Succeeded)
                {
                    return IdentityFailure(validation);
                }
            }

            var normalizedCode = NormalizeRecoveryCode(request.RecoveryCode);
            var candidates = await _dbContext.RootRecoveryCodes
                .Where(x => x.ApplicationUserId == user.Id && x.UsedAt == null)
                .ToListAsync(cancellationToken);
            var match = candidates.FirstOrDefault(x =>
                _userManager.PasswordHasher.VerifyHashedPassword(user, x.CodeHash, normalizedCode) != PasswordVerificationResult.Failed);
            if (match is null)
            {
                return Failure("invalid_recovery", "The recovery details are invalid.");
            }

            await using var transaction = _dbContext.Database.IsRelational()
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var claimed = _dbContext.Database.IsRelational()
                ? await _dbContext.RootRecoveryCodes
                    .Where(x => x.Id == match.Id && x.UsedAt == null && x.ConcurrencyStamp == match.ConcurrencyStamp)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.UsedAt, DateTimeOffset.UtcNow)
                        .SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid().ToString()), cancellationToken)
                : await ClaimInMemoryAsync(match, cancellationToken);
            if (claimed != 1)
            {
                return Failure("invalid_recovery", "The recovery details are invalid.");
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await _userManager.ResetPasswordAsync(user, token, request.Password);
            if (!reset.Succeeded)
            {
                return IdentityFailure(reset);
            }

            await _userManager.UpdateSecurityStampAsync(user);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            await _auditWriter.WriteAsync(
                "authentication.root_recovered",
                "ApplicationUser",
                user.Id,
                "The root administrator password was reset with a recovery code.",
                cancellationToken: cancellationToken);
            return Success("Password reset successfully. The recovery code has been consumed.");
        }
        finally
        {
            RecoveryLock.Release();
        }
    }

    public async Task<AuthActionResponse> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsInitialAdministrator)
        {
            return Failure("not_root_administrator", "Recovery codes are only available to the root administrator.");
        }

        var codes = await ReplaceRecoveryCodesAsync(user, cancellationToken);
        await _userManager.UpdateSecurityStampAsync(user);
        await _signInManager.RefreshSignInAsync(user);
        return new AuthActionResponse(true, null, "New recovery codes generated. All previous codes are invalid.", RecoveryCodes: codes);
    }

    public Task LogoutAsync() => _signInManager.SignOutAsync();

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.RootRecoveryCodes
            .Where(x => x.ApplicationUserId == user.Id)
            .ToListAsync(cancellationToken);
        _dbContext.RootRecoveryCodes.RemoveRange(existing);

        var uniqueCodes = new HashSet<string>(StringComparer.Ordinal);
        while (uniqueCodes.Count < 10)
        {
            uniqueCodes.Add(GenerateRecoveryCode());
        }
        var plaintext = uniqueCodes.ToArray();
        var createdAt = DateTimeOffset.UtcNow;
        foreach (var code in plaintext)
        {
            _dbContext.RootRecoveryCodes.Add(new RootRecoveryCode
            {
                Id = Guid.NewGuid(),
                ApplicationUserId = user.Id,
                CodeHash = _userManager.PasswordHasher.HashPassword(user, NormalizeRecoveryCode(code)),
                CreatedAt = createdAt
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return plaintext;
    }

    private async Task<int> ClaimInMemoryAsync(RootRecoveryCode code, CancellationToken cancellationToken)
    {
        if (code.UsedAt.HasValue)
        {
            return 0;
        }

        code.UsedAt = DateTimeOffset.UtcNow;
        code.ConcurrencyStamp = Guid.NewGuid().ToString();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return 1;
    }

    private static string GenerateRecoveryCode()
    {
        var characters = Enumerable.Range(0, 12)
            .Select(_ => RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)])
            .ToArray();
        return $"{new string(characters, 0, 4)}-{new string(characters, 4, 4)}-{new string(characters, 8, 4)}";
    }

    private static string NormalizeRecoveryCode(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private async Task ReconcileExistingOrganizationsAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var organizations = await _dbContext.CoreOrganizations.ToListAsync(cancellationToken);
        foreach (var organization in organizations)
        {
            var owner = await _dbContext.CoreOrganizationUsers
                .FirstOrDefaultAsync(x => x.OrganizationId == organization.Id &&
                    x.EmployeeType == EmployeeType.Human &&
                    x.PermissionLevel == OrganizationPermissionLevel.Owner &&
                    x.ApplicationUserId == null, cancellationToken);
            var ceoRoleId = await _dbContext.CoreRoles
                .Where(x => x.OrganizationId == organization.Id && x.Name == "CEO")
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (owner is null)
            {
                owner = new OrganizationUser
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organization.Id,
                    RoleId = ceoRoleId,
                    DisplayName = user.DisplayName,
                    EmployeeType = EmployeeType.Human,
                    PermissionLevel = OrganizationPermissionLevel.Owner,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                _dbContext.CoreOrganizationUsers.Add(owner);
            }

            owner.ApplicationUserId = user.Id;
            owner.Email = user.Email;
            owner.DisplayName = user.DisplayName;
            if (!await _dbContext.LeadershipAssignments.AnyAsync(x =>
                    x.OrganizationId == organization.Id &&
                    x.PositionKey == LeadershipPositionKeys.ChiefExecutiveOfficer && x.EndsAt == null,
                    cancellationToken))
            {
                _dbContext.LeadershipAssignments.Add(new LeadershipAssignment
                {
                    Id = Guid.NewGuid(), OrganizationId = organization.Id, OrganizationUserId = owner.Id,
                    PositionKey = LeadershipPositionKeys.ChiefExecutiveOfficer,
                    StartsAt = DateTimeOffset.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static bool TryDecodeToken(string code, out string token)
    {
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            return true;
        }
        catch (FormatException)
        {
            token = string.Empty;
            return false;
        }
    }

    private static AuthActionResponse Success(string message, AuthStatusResponse? session = null) => new(true, null, message, session);
    private static AuthActionResponse Failure(string code, string message) => new(false, code, message);
    private static AuthActionResponse Validation(string code, string message, string field) =>
        new(false, code, message, ValidationErrors: new Dictionary<string, string[]> { [field] = [message] });

    private static AuthActionResponse IdentityFailure(IdentityResult result, string code = "validation_error")
    {
        var errors = result.Errors
            .GroupBy(x => x.Code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "Password" : "Email")
            .ToDictionary(x => x.Key, x => x.Select(y => y.Description).ToArray());
        return new AuthActionResponse(false, code, "The account information is invalid.", ValidationErrors: errors);
    }
}
