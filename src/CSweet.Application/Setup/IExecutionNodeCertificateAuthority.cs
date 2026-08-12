namespace CSweet.Application.Setup;

public sealed record IssuedExecutionNodeCertificate(
    string CertificateBase64,
    string Thumbprint,
    string SerialNumber,
    DateTimeOffset ExpiresAt);

public interface IExecutionNodeCertificateAuthority
{
    IssuedExecutionNodeCertificate Issue(string certificateSigningRequestPem, Guid nodeId);
}
