using System.Net.Mail;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Auth;

public sealed class EmailDeliveryProfileService : IEmailDeliveryProfileService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IEmailDeliveryConfigurationProvider _provider;
    private readonly IAccountEmailSender _sender;

    public EmailDeliveryProfileService(
        CSweetDbContext dbContext,
        IEmailDeliveryConfigurationProvider provider,
        IAccountEmailSender sender)
    {
        _dbContext = dbContext;
        _provider = provider;
        _sender = sender;
    }

    public async Task<IReadOnlyList<EmailDeliveryProfileResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _dbContext.EmailDeliveryProfiles
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<EmailDeliveryProfileActionResponse> CreateAsync(
        SaveEmailDeliveryProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (validation is not null) return validation;

        var now = DateTimeOffset.UtcNow;
        var profile = new EmailDeliveryProfile
        {
            Id = Guid.NewGuid(),
            ConfiguredAt = now,
            IsDefault = !await _dbContext.EmailDeliveryProfiles.AnyAsync(cancellationToken)
        };
        Apply(profile, request, now);
        _dbContext.EmailDeliveryProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await SuccessAsync("Email delivery profile saved.", profile, cancellationToken);
    }

    public async Task<EmailDeliveryProfileActionResponse> UpdateAsync(
        Guid id,
        SaveEmailDeliveryProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (validation is not null) return validation;

        var profile = await _dbContext.EmailDeliveryProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return Failure("not_found", "Email delivery profile was not found.");

        Apply(profile, request, DateTimeOffset.UtcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await SuccessAsync("Email delivery profile updated.", profile, cancellationToken);
    }

    public async Task<EmailDeliveryProfileActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.EmailDeliveryProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return Failure("not_found", "Email delivery profile was not found.");

        var wasDefault = profile.IsDefault;
        _dbContext.EmailDeliveryProfiles.Remove(profile);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (wasDefault)
        {
            var replacement = await _dbContext.EmailDeliveryProfiles
                .OrderByDescending(x => x.LastTestSucceededAt.HasValue)
                .ThenByDescending(x => x.LastTestSucceededAt)
                .ThenBy(x => x.ConfiguredAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (replacement is not null)
            {
                replacement.IsDefault = true;
                replacement.UpdatedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return new EmailDeliveryProfileActionResponse(
            true,
            null,
            "Email delivery profile deleted.",
            Profiles: await ListAsync(cancellationToken));
    }

    public async Task<EmailDeliveryProfileActionResponse> TestAsync(
        Guid id,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.EmailDeliveryProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return Failure("not_found", "Email delivery profile was not found.");

        var email = await _dbContext.Users
            .Where(x => x.Id == applicationUserId)
            .Select(x => x.Email)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(email)) return Failure("user_not_found", "The administrator email could not be found.");

        try
        {
            await _sender.SendTestAsync(email, id, cancellationToken);
        }
        catch
        {
            profile.LastTestSucceededAt = null;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Failure("email_delivery_failed", "The test email could not be delivered. Check the SMTP settings and try again.");
        }

        profile.LastTestSucceededAt = DateTimeOffset.UtcNow;
        profile.UpdatedAt = profile.LastTestSucceededAt.Value;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await SuccessAsync($"Test email sent to {email}.", profile, cancellationToken);
    }

    public async Task<EmailDeliveryProfileActionResponse> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profiles = await _dbContext.EmailDeliveryProfiles.ToListAsync(cancellationToken);
        var selected = profiles.SingleOrDefault(x => x.Id == id);
        if (selected is null) return Failure("not_found", "Email delivery profile was not found.");

        var now = DateTimeOffset.UtcNow;
        foreach (var profile in profiles.Where(x => x.IsDefault && x.Id != id))
        {
            profile.IsDefault = false;
            profile.UpdatedAt = now;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        selected.IsDefault = true;
        selected.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await SuccessAsync("Default email delivery profile updated.", selected, cancellationToken);
    }

    public async Task<bool> HasReadyDefaultAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _provider.GetAsync(cancellationToken);
        return settings.IsConfigured && settings.LastTestSucceededAt.HasValue;
    }

    private void Apply(EmailDeliveryProfile profile, SaveEmailDeliveryProfileRequest request, DateTimeOffset now)
    {
        profile.Name = request.Name.Trim();
        profile.ProviderKey = EmailDeliveryProviderKeys.CustomSmtp;
        profile.Host = request.Host.Trim();
        profile.Port = request.Port;
        profile.EnableSsl = request.EnableSsl;
        profile.UserName = TrimOrNull(request.UserName);
        profile.FromAddress = request.FromAddress.Trim();
        profile.FromName = request.FromName.Trim();
        profile.PublicAppUrl = request.PublicAppUrl.TrimEnd('/');
        profile.LastTestSucceededAt = null;
        profile.UpdatedAt = now;

        if (request.ClearPassword)
        {
            profile.EncryptedPassword = null;
        }
        else if (request.Password is not null)
        {
            profile.EncryptedPassword = string.IsNullOrEmpty(request.Password) ? null : _provider.Encrypt(request.Password);
        }
    }

    private static EmailDeliveryProfileActionResponse? Validate(SaveEmailDeliveryProfileRequest request)
    {
        if (!string.Equals(request.ProviderKey, EmailDeliveryProviderKeys.CustomSmtp, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160 ||
            string.IsNullOrWhiteSpace(request.Host) || request.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(request.FromAddress) || string.IsNullOrWhiteSpace(request.FromName) ||
            !Uri.TryCreate(request.PublicAppUrl, UriKind.Absolute, out _))
        {
            return Failure("validation_error", "Complete the required email delivery fields with valid values.");
        }

        try { _ = new MailAddress(request.FromAddress.Trim()); }
        catch (FormatException) { return Failure("validation_error", "Sender email address is invalid."); }
        return null;
    }

    private async Task<EmailDeliveryProfileActionResponse> SuccessAsync(
        string message,
        EmailDeliveryProfile profile,
        CancellationToken cancellationToken) =>
        new(true, null, message, ToResponse(profile), await ListAsync(cancellationToken));

    private static EmailDeliveryProfileResponse ToResponse(EmailDeliveryProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.ProviderKey,
        profile.Host,
        profile.Port,
        profile.EnableSsl,
        profile.UserName,
        !string.IsNullOrWhiteSpace(profile.EncryptedPassword),
        profile.FromAddress,
        profile.FromName,
        profile.PublicAppUrl,
        IsDefault: profile.IsDefault,
        IsConfigured: true,
        IsReady: profile.LastTestSucceededAt.HasValue,
        profile.ConfiguredAt,
        profile.LastTestSucceededAt,
        profile.UpdatedAt);

    private static EmailDeliveryProfileActionResponse Failure(string code, string message) => new(false, code, message);
    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
