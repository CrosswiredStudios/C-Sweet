using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

/// <summary>Owned by one delivery worker. Dispose only after that worker has exited.</summary>
internal sealed class HyperVResultDeliveryConnection(
    ComputeProviderConfiguration configuration, TimeProvider clock,
    Func<ComputeProviderConfiguration, TimeProvider, X509Certificate2> openCertificate,
    Func<Uri, ComputeResultHttpClient>? createTransport = null) : IDisposable
{
    private X509Certificate2? certificate;
    private ComputeResultHttpClient? transport;
    private bool disposed;

    public async Task<ComputeResultAcknowledgement> DeliverAsync(ComputeResultOutboxEntry row, CancellationToken token)
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

                var origin = new Uri(configuration.CoreOrigin);
                transport = createTransport is null ? new(origin, configuration.CoreCertificateSha256) : createTransport(origin);
            }
            var signer = new ComputeProviderResultSigner(configuration.Enrollment, certificate!, configuration.NodeSigningIdentity.KeyId, clock);
            return await transport.DeliverAsync(signer.Sign(row.Result), token);
        }
        catch (InvalidDataException) when (!token.IsCancellationRequested && row.Result.ExpiresAt <= clock.GetUtcNow())
        {
            throw new ComputeResultExpiredException();
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is CryptographicException or InvalidOperationException or UnauthorizedAccessException)
        {
            Close();
            // The existing worker owns capped backoff. Credential repair must not restart local TTL
            // enforcement or change the immutable observation being retried.
            throw new IOException("The enrolled result signing credential is unavailable.");
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
