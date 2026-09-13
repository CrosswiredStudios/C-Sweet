using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

/// <summary>Owned by one delivery worker. Dispose only after that worker has exited.</summary>
internal sealed class HyperVMaintenanceDeliveryConnection(
    ComputeProviderConfiguration configuration, TimeProvider clock,
    Func<ComputeProviderConfiguration, TimeProvider, X509Certificate2> openCertificate,
    Func<Uri, ComputeMaintenanceSigner, ComputeMaintenanceHttpClient>? createTransport = null) : IDisposable
{
    private X509Certificate2? certificate;
    private ComputeMaintenanceHttpClient? transport;
    private bool disposed;

    public async Task DeliverAsync(ComputeMaintenanceOutboxEntry row, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        try
        {
            if (transport is null)
            {
                // Every recovery uses the same configured thumbprint and enrolled public identity.
                // No configuration reload or arbitrary certificate fallback occurs here.
                certificate = openCertificate(configuration, clock);
                var signer = new ComputeMaintenanceSigner(certificate, configuration.NodeSigningIdentity.KeyId, clock);
                var origin = new Uri(configuration.CoreOrigin);
                transport = createTransport is null ? new(origin, signer, configuration.CoreCertificateSha256) : createTransport(origin, signer);
            }
            await transport.DeliverAsync(row, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is CryptographicException or InvalidOperationException or UnauthorizedAccessException)
        {
            Close();
            // The existing worker owns capped backoff. Credential repair must not restart local TTL
            // enforcement or change the immutable event being retried.
            throw new IOException("The enrolled maintenance signing credential is unavailable.");
        }
        catch
        {
            if (transport is null) Close();
            throw;
        }
    }

    private void Close()
    {
        transport?.Dispose(); transport = null;
        certificate?.Dispose(); certificate = null;
    }

    public void Dispose() { disposed = true; Close(); }
}
