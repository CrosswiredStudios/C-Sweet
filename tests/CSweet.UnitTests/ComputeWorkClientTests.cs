using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Infrastructure.Compute;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeWorkClientTests
{
    [Fact]
    public async Task Signed_client_discovers_and_recovers_verified_observation_authority()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var certificate = Certificate(f, key);
        var trust = new ComputeResultTests.Trust(new("node", f.Enrollment.OrganizationId, f.Enrollment.NodeId, f.Enrollment.ProviderId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var service = new ComputeProviderWorkService(f.Core.Broker.Db, new(trust, f.Core.Time), f.Core.Authorizer(), f.Core.Broker.Ledger, f.Core.Time);
        using var client = Client(f, certificate, new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.Headers);
            var signed = JsonSerializer.Deserialize<SignedComputeProviderWorkRequest>(await request.Content!.ReadAsStringAsync(token), ComputeProtocol.Json)!;
            if (request.RequestUri!.AbsolutePath == ComputeWorkTransport.DiscoveryPath) return Reply(await service.DiscoverAsync(signed, token));
            var packet = await service.ClaimAsync(signed, token);
            return packet is null ? new(HttpStatusCode.NoContent) : Reply(packet);
        }));
        Assert.Empty((await client.DiscoverAsync(null, default)).OperationIds);
        f.Core.Time.Now = f.Core.Time.Now.AddMinutes(1);
        Assert.Equal(f.Core.OperationId, Assert.Single((await client.DiscoverAsync(null, default)).OperationIds));
        var packet = await client.ClaimAsync(f.Core.OperationId, default);
        Assert.NotNull(packet); Assert.Equal(ComputeDispatchMode.Observe, f.Verifier.Verify(packet).Authorization.Mode);
        Assert.Null(await client.ClaimAsync(f.Core.OperationId, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_rejects_invalid_signature_or_valid_authority_for_different_operation(bool invalidSignature)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var certificate = Certificate(f, key);
        var packet = invalidSignature ? f.Packet with { Authorization = f.Packet.Authorization with { SignatureBase64 = "invalid" } } : f.Packet;
        using var client = Client(f, certificate, new Handler((_, _) => Task.FromResult(Reply(packet))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ClaimAsync(invalidSignature ? f.Core.OperationId : Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Discovery_rejects_duplicate_ids_and_signer_rejects_ambiguous_selectors_or_expiring_certificate()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var certificate = Certificate(f, key);
        using var client = Client(f, certificate, new Handler((_, _) => Task.FromResult(Reply(new ComputeProviderWorkPage([f.Core.OperationId, f.Core.OperationId], null)))));
        await Assert.ThrowsAsync<IOException>(() => client.DiscoverAsync(null, default));
        var signer = new ComputeProviderWorkSigner(f.Enrollment, certificate, "node", f.Core.Time);
        Assert.Throws<InvalidDataException>(() => signer.Sign(Guid.NewGuid(), Guid.NewGuid()));
        f.Core.Time.Now = certificate.NotAfter.ToUniversalTime().AddSeconds(-30);
        Assert.Throws<InvalidOperationException>(() => signer.Sign());
    }

    private static ComputeWorkHttpClient Client(Fixture f, X509Certificate2 certificate, HttpMessageHandler handler) =>
        new(new("https://core.example"), new(f.Enrollment, certificate, "node", f.Core.Time), f.Verifier, handler);
    private static X509Certificate2 Certificate(Fixture f, ECDsa key) => new CertificateRequest("CN=compute-work-client", key, HashAlgorithmName.SHA256)
        .CreateSelfSigned(f.Core.Time.Now.AddDays(-1), f.Core.Time.Now.AddDays(1));
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, ComputeProtocol.Json), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
