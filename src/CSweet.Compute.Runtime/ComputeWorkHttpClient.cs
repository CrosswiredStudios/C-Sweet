using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Service-owned work recovery. Discovery is a hint; returned dispatches are independently verified.</summary>
public sealed class ComputeWorkHttpClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri origin;
    private readonly ComputeProviderWorkSigner signer;
    private readonly ComputeDispatchVerifier verifier;

    public ComputeWorkHttpClient(Uri coreOrigin, ComputeProviderWorkSigner signer, ComputeDispatchVerifier verifier, string? certificateSha256 = null)
        : this(coreOrigin, signer, verifier, ComputeMaintenanceHttpClient.CreateHandler(certificateSha256)) { }

    internal ComputeWorkHttpClient(Uri coreOrigin, ComputeProviderWorkSigner signer, ComputeDispatchVerifier verifier, HttpMessageHandler handler)
    {
        if (!coreOrigin.IsAbsoluteUri || coreOrigin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(coreOrigin.UserInfo) ||
            !string.IsNullOrEmpty(coreOrigin.Query) || !string.IsNullOrEmpty(coreOrigin.Fragment) || coreOrigin.AbsolutePath != "/")
            throw new ArgumentException("A Core HTTPS origin without credentials, query, fragment or path is required.");
        origin = coreOrigin; this.signer = signer; this.verifier = verifier;
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<bool> AuthorizePublicationAsync(Guid operationId, CancellationToken token)
    {
        var permission = await SendAsync<ComputePublicationPermission>(ComputeWorkTransport.PublicationAccessPath, signer.Sign(operationId), false, token);
        if (permission?.OperationId != operationId) throw new IOException("Publication authorization did not match.");
        return permission.Allowed;
    }
    public async Task<ComputeProviderWakeHint?> WaitForWakeAsync(CancellationToken token)
    {
        var hint = await SendAsync<ComputeProviderWakeHint>(ComputeProviderWakeTransport.Path, signer.Sign(), true, token);
        if (hint?.EventId == Guid.Empty) throw new IOException("Core returned an invalid wake identity.");
        return hint;
    }
    public async Task<ComputeProviderWorkPage> DiscoverAsync(Guid? afterOperationId, CancellationToken token)
    {
        var page = await SendAsync<ComputeProviderWorkPage>(ComputeWorkTransport.DiscoveryPath, signer.Sign(afterOperationId: afterOperationId), false, token)
            ?? throw new IOException("Core work discovery returned no page.");
        if (page.OperationIds is null || page.OperationIds.Count > 100 || page.OperationIds.Any(x => x == Guid.Empty ||
            afterOperationId.HasValue && x.CompareTo(afterOperationId.Value) <= 0) ||
            !page.OperationIds.SequenceEqual(page.OperationIds.Distinct().Order()) ||
            page.NextAfterOperationId != (page.OperationIds.Count == 100 ? page.OperationIds[^1] : (Guid?)null))
            throw new IOException("Core work discovery returned an invalid page.");
        return page;
    }

    public async Task<ComputeDispatchPacket?> ClaimAsync(Guid operationId, CancellationToken token)
    {
        var packet = await SendAsync<ComputeDispatchPacket>(ComputeWorkTransport.ClaimPath, signer.Sign(operationId), true, token);
        if (packet is not null && verifier.Verify(packet).Authorization.OperationId != operationId)
            throw new UnauthorizedAccessException("Core returned authority for another operation.");
        return packet;
    }

    public async Task<ComputeResultAcknowledgement?> ReadResultReceiptAsync(ComputeResultOutboxEntry row, CancellationToken token)
    {
        var receipt = await SendAsync<ComputeResultAcknowledgement>(ComputeWorkTransport.ReceiptPath,
            signer.Sign(row.Result.OperationId, resultSequence: row.Result.Sequence, resultDigest: row.Digest), true, token);
        if (receipt is not null && (receipt.OperationId != row.Result.OperationId || receipt.Sequence != row.Result.Sequence ||
            receipt.PayloadDigest != row.Digest || receipt.Applied || !Enum.IsDefined(receipt.Disposition)))
            throw new IOException("Core receipt does not match the queried evidence.");
        return receipt;
    }

    private async Task<T?> SendAsync<T>(string path, SignedComputeProviderWorkRequest envelope, bool allowEmpty, CancellationToken token) where T : class
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ComputeProtocol.Json);
        if (bytes.Length > ComputeWorkTransport.MaximumRequestBytes) throw new InvalidDataException("Work request exceeds its limit.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path)) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (allowEmpty && response.StatusCode == HttpStatusCode.NoContent) return null;
        const int maximumResponseBytes = 256 * 1024;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > maximumResponseBytes)
            throw new IOException("Core work request did not return a valid response.");
        using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        while ((count = await stream.ReadAsync(buffer, linked.Token)) > 0)
        {
            if (body.Length + count > maximumResponseBytes) throw new IOException("Core work response exceeds its limit.");
            body.Write(buffer, 0, count);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), ComputeProtocol.Json)
                ?? throw new IOException("Core work response is missing.");
        }
        catch (JsonException) { throw new IOException("Core work response is invalid."); }
    }

    public void Dispose() => client.Dispose();
}
