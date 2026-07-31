namespace CSweet.Infrastructure.Auth;

public interface IAccountEmailSender
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
    Task SendConfirmationAsync(string email, Guid userId, string code, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(string email, Guid userId, string code, CancellationToken cancellationToken);
    Task SendTestAsync(string email, Guid profileId, CancellationToken cancellationToken);
}
