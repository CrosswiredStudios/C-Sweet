using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

/// <summary>Owned by one delivery worker. Dispose only after that worker has exited.</summary>
internal sealed class HyperVWorkConnection(
    ComputeProviderConfiguration configuration, ComputeDispatchVerifier verifier, TimeProvider clock,
    Func<ComputeProviderConfiguration, TimeProvider, X509Certificate2> openCertificate,
    Func<Uri, ComputeProviderWorkSigner, ComputeWorkHttpClient>? createTransport = null) : IDisposable
{
    private X509Certificate2? certificate;
    private ComputeWorkHttpClient? transport;
    private bool disposed;

    public Task<bool> AuthorizePublicationAsync(Guid operationId, CancellationToken token) =>
        UseAsync((client, cancellation) => client.AuthorizePublicationAsync(operationId, cancellation), token);
    public Task<ComputeProviderWakeHint?> WaitForWakeAsync(CancellationToken token) =>
        UseAsync((client, cancellation) => client.WaitForWakeAsync(cancellation), token);

    public Task<ComputeProviderWorkPage> DiscoverAsync(Guid? afterOperationId, CancellationToken token) =>
        UseAsync((client, cancellation) => client.DiscoverAsync(afterOperationId, cancellation), token);

    public Task<ComputeDispatchPacket?> ClaimAsync(Guid operationId, CancellationToken token) =>
        UseAsync((client, cancellation) => client.ClaimAsync(operationId, cancellation), token);

    public Task<ComputeResultAcknowledgement?> ReadResultReceiptAsync(ComputeResultOutboxEntry row, CancellationToken token) =>
        UseAsync((client, cancellation) => client.ReadResultReceiptAsync(row, cancellation), token);

    private async Task<T> UseAsync<T>(Func<ComputeWorkHttpClient, CancellationToken, Task<T>> use, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        try
        {
            if (transport is null)
            {
                // Every recovery uses the same configured thumbprint and enrolled public identity.
                // No configuration reload or arbitrary certificate fallback occurs here.
                try { certificate = openCertificate(configuration, clock); }
                catch (UnauthorizedAccessException) { throw new IOException("The enrolled work credential is unavailable."); }

                var origin = new Uri(configuration.CoreOrigin);
                var signer = new ComputeProviderWorkSigner(configuration.Enrollment, certificate, configuration.NodeSigningIdentity.KeyId, clock);
                transport = createTransport is null ? new(origin, signer, verifier, configuration.CoreCertificateSha256) : createTransport(origin, signer);
            }
            return await use(transport, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is CryptographicException or InvalidOperationException)
        {
            Close();
            // The existing worker owns capped backoff. Credential repair must not restart local TTL
            // enforcement or change the operation identity being retried.
            throw new IOException("The enrolled work signing credential is unavailable.");
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
