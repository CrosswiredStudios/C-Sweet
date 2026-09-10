using System.Security.Cryptography.X509Certificates;
using CSweet.Domain.Setup;

namespace CSweet.ExecutionGateway;

/// <summary>Authentication is bound to a TLS connection, not to a renewable certificate's serial.
/// New connections must present the exact current certificate. Existing connections recheck the
/// approved identity and revocation on every message, allowing renewal without cancelling work.</summary>
internal static class OfficeConnectionIdentity
{
    private static readonly object IdentityKey = new();
    private sealed record Binding(Guid OfficeId, string Csr, string Thumbprint);

    internal static bool Authorize(ExecutionNode node, X509Certificate2? certificate,
        IDictionary<object, object?> items, DateTimeOffset now)
    {
        if (certificate is null || node.ApprovedAt is null ||
            node.Status is not (ExecutionNodeStatus.Ready or ExecutionNodeStatus.Offline or ExecutionNodeStatus.Draining) ||
            node.CertificateExpiresAt <= now)
            return false;
        lock (items)
        {
            if (items.TryGetValue(IdentityKey, out var value) && value is Binding binding)
                return binding.OfficeId == node.Id && binding.Csr == node.CertificateSigningRequestPem &&
                    binding.Thumbprint == Normalize(certificate.Thumbprint);
            if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now ||
                Normalize(certificate.Thumbprint) != node.CertificateThumbprint ||
                Normalize(certificate.SerialNumber) != node.CertificateSerialNumber)
                return false;
            items[IdentityKey] = new Binding(node.Id, node.CertificateSigningRequestPem, Normalize(certificate.Thumbprint));
            return true;
        }
    }

    private static string Normalize(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
