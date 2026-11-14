namespace CSweet.Application.Auth;

/// <summary>
/// The C-Sweet account that approved hosted setup and the installation the issuer registered for it.
/// <paramref name="PrivateKey"/> is the installation key (PKCS#8); the service protects it before storing.
/// </summary>
public sealed record HostedAdministratorLink(
    string Issuer,
    string Subject,
    string Email,
    Guid InstallationId,
    string InstallationName,
    byte[] PrivateKey);
