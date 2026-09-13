using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Delivers immutable signed observations; success acknowledges receipt, not physical state.</summary>
public sealed class ComputeResultHttpClient : IDisposable
{
    private const int MaximumAcknowledgementBytes = 4096;
    private readonly HttpClient client;
    private readonly Uri endpoint;

    public ComputeResultHttpClient(Uri coreOrigin, string? certificateSha256 = null) : this(coreOrigin, ComputeMaintenanceHttpClient.CreateHandler(certificateSha256)) { }

    internal ComputeResultHttpClient(Uri coreOrigin, HttpMessageHandler handler)
    {
        if (!coreOrigin.IsAbsoluteUri || coreOrigin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(coreOrigin.UserInfo) ||
            !string.IsNullOrEmpty(coreOrigin.Query) || !string.IsNullOrEmpty(coreOrigin.Fragment) || coreOrigin.AbsolutePath != "/")
            throw new ArgumentException("A Core HTTPS origin without credentials, query, fragment or path is required.");
        endpoint = new(coreOrigin, ComputeResultTransport.Path);
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ComputeResultAcknowledgement> DeliverAsync(SignedComputeProviderResult envelope, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ComputeProtocol.Json);
        if (bytes.Length > ComputeResultTransport.MaximumRequestBytes)
            throw new InvalidDataException("Provider result exceeds its size limit.");
        var result = JsonSerializer.Deserialize<ComputeProviderResult>(envelope.PayloadJson, ComputeProtocol.Json)
            ?? throw new InvalidDataException("Provider result is missing.");
        var digest = ComputeProtocol.Digest(envelope.PayloadJson);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > MaximumAcknowledgementBytes)
            throw new IOException("Core did not acknowledge the provider result.");
        using var body = new MemoryStream(); var buffer = new byte[1024]; int count;
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        while ((count = await stream.ReadAsync(buffer, linked.Token)) > 0)
        {
            if (body.Length + count > MaximumAcknowledgementBytes)
                throw new IOException("Core result acknowledgement exceeds its size limit.");
            body.Write(buffer, 0, count);
        }
        ComputeResultAcknowledgement? acknowledgement;
        try { acknowledgement = JsonSerializer.Deserialize<ComputeResultAcknowledgement>(body.GetBuffer().AsSpan(0, (int)body.Length), ComputeProtocol.Json); }
        catch (JsonException) { throw new IOException("Core result acknowledgement is invalid."); }
        if (acknowledgement?.OperationId != result.OperationId || acknowledgement.Sequence != result.Sequence || acknowledgement.PayloadDigest != digest || acknowledgement.Disposition != ComputeResultDisposition.Recorded)
            throw new IOException("Core result acknowledgement does not match the delivered observation.");
        return acknowledgement;
    }

    public void Dispose() => client.Dispose();
}
