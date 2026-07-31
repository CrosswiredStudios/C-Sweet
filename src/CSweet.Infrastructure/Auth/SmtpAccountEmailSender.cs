using System.Net;
using System.Net.Mail;

namespace CSweet.Infrastructure.Auth;

public sealed class SmtpAccountEmailSender : IAccountEmailSender
{
    private readonly IEmailDeliveryConfigurationProvider _provider;

    public SmtpAccountEmailSender(IEmailDeliveryConfigurationProvider provider)
    {
        _provider = provider;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        (await _provider.GetAsync(cancellationToken)).IsConfigured;

    public Task SendConfirmationAsync(string email, Guid userId, string code, CancellationToken cancellationToken)
    {
        return SendConfirmationCoreAsync(email, userId, code, cancellationToken);
    }

    private async Task SendConfirmationCoreAsync(string email, Guid userId, string code, CancellationToken cancellationToken)
    {
        var settings = await _provider.GetAsync(cancellationToken);
        var link = BuildLink(settings, "confirm-email", userId, code);
        await SendAsync(email, "Confirm your C-Sweet administrator account",
            $"Confirm your C-Sweet administrator account by opening this link:\n\n{link}\n\nIf you did not request this account, you can ignore this message.", settings, cancellationToken);
    }

    public Task SendPasswordResetAsync(string email, Guid userId, string code, CancellationToken cancellationToken)
    {
        return SendPasswordResetCoreAsync(email, userId, code, cancellationToken);
    }

    private async Task SendPasswordResetCoreAsync(string email, Guid userId, string code, CancellationToken cancellationToken)
    {
        var settings = await _provider.GetAsync(cancellationToken);
        var link = BuildLink(settings, "reset-password", userId, code);
        await SendAsync(email, "Reset your C-Sweet password",
            $"Reset your C-Sweet password by opening this link:\n\n{link}\n\nIf you did not request a reset, you can ignore this message.", settings, cancellationToken);
    }

    public async Task SendTestAsync(string email, Guid profileId, CancellationToken cancellationToken)
    {
        var settings = await _provider.GetAsync(profileId, cancellationToken);
        await SendAsync(email, "C-Sweet email delivery test",
            "Email delivery is configured correctly for this C-Sweet instance.", settings, cancellationToken);
    }

    private static string BuildLink(EffectiveEmailDeliverySettings settings, string path, Guid userId, string code)
    {
        var root = settings.PublicAppUrl.TrimEnd('/');
        return $"{root}/{path}?userId={Uri.EscapeDataString(userId.ToString())}&code={Uri.EscapeDataString(code)}";
    }

    private static async Task SendAsync(string email, string subject, string body, EffectiveEmailDeliverySettings settings, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("SMTP email delivery is not configured.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(email));

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrWhiteSpace(settings.UserName))
        {
            client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
