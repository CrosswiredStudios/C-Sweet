using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Service-owned delivery to a configured Core HTTPS origin; no cookies, ambient credentials or redirects.</summary>
public sealed class ComputeMaintenanceHttpClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly ComputeMaintenanceSigner signer;

    public ComputeMaintenanceHttpClient(Uri coreOrigin, ComputeMaintenanceSigner signer, string? certificateSha256 = null)
        : this(coreOrigin, signer, CreateHandler(certificateSha256)) { }

    internal static HttpClientHandler CreateHandler(string? certificateSha256 = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        if (certificateSha256 is null) return handler;
        if (certificateSha256.Length != 64 || !certificateSha256.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid TLS pin.");
        var expected = Convert.FromHexString(certificateSha256);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
            (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected,
                certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
        return handler;
    }

    // Tests may inject a recording handler or a pinned loopback TLS certificate. Production uses normal TLS validation above.
    internal ComputeMaintenanceHttpClient(Uri coreOrigin, ComputeMaintenanceSigner signer, HttpMessageHandler handler)
    {
        if (!coreOrigin.IsAbsoluteUri || coreOrigin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(coreOrigin.UserInfo) ||
            !string.IsNullOrEmpty(coreOrigin.Query) || !string.IsNullOrEmpty(coreOrigin.Fragment) || coreOrigin.AbsolutePath != "/")
            throw new ArgumentException("A Core HTTPS origin without credentials, query, fragment or path is required.");
        endpoint = new(coreOrigin, ComputeMaintenanceTransport.Path); this.signer = signer;
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task DeliverAsync(ComputeMaintenanceOutboxEntry entry, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(signer.Sign(entry), ComputeProtocol.Json);
        if (bytes.Length > ComputeMaintenanceTransport.MaximumRequestBytes) throw new InvalidDataException("Maintenance delivery exceeds its size limit.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > ComputeMaintenanceTransport.MaximumAcknowledgementBytes)
            throw new IOException("Core did not acknowledge maintenance delivery.");
        using var body = new MemoryStream(); var buffer = new byte[1024]; int count;
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        while ((count = await stream.ReadAsync(buffer, linked.Token)) > 0)
        {
            if (body.Length + count > ComputeMaintenanceTransport.MaximumAcknowledgementBytes)
                throw new IOException("Core maintenance acknowledgement exceeds its size limit.");
            body.Write(buffer, 0, count);
        }
        ComputeMaintenanceAcknowledgement? acknowledgement;
        try { acknowledgement = JsonSerializer.Deserialize<ComputeMaintenanceAcknowledgement>(body.GetBuffer().AsSpan(0, (int)body.Length), ComputeProtocol.Json); }
        catch (JsonException) { throw new IOException("Core maintenance acknowledgement is invalid."); }
        if (acknowledgement?.EventId != entry.Id || acknowledgement.EventDigest != entry.Digest)
            throw new IOException("Core maintenance acknowledgement does not match queued evidence.");
    }

    public void Dispose() => client.Dispose();
}
