using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeResultConnectionTests
{
    [Fact]
    public async Task Expiry_during_credential_open_retains_original_evidence_without_sending()
    {
        await using var f = new ComputeReplayJournalTests.Fixture(); await f.InitializeAsync();
        await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
            Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning))), default);
        var row = Assert.Single(await f.Journal().ListResultsAsync(100, default));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=result-expiry-race", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(f.Core.Time.Now.AddDays(-1), f.Core.Time.Now.AddDays(1));
        var config = new ComputeProviderConfiguration(1, f.Enrollment, new("node-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
            "https://core.example", f.Root, Path.Combine(f.Root, "workloads"), new(10, new(40, 81920, 409600)), certificate.Thumbprint, StoreLocation.LocalMachine);
        using var connection = new HyperVResultDeliveryConnection(config, f.Core.Time, (_, _) =>
        { f.Core.Time.Now = row.Result.ExpiresAt; return new X509Certificate2(certificate); },
            origin => new(origin, new Handler((_, _) => throw new InvalidOperationException("Expired result must never be sent."))));
        var pass = await new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time).DispatchAsync(null, connection.DeliverAsync, default);
        Assert.Equal(row, Assert.Single(pass.RequiresObservation)); Assert.Equal(0, pass.Acknowledged);
        Assert.Equal(row, Assert.Single(await f.Journal().ListResultsAsync(100, default)));
    }

    [Fact]
    public async Task Owned_connection_signs_for_enrolled_identity_and_core_reconciles_exact_payload()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var clock = new ComputeBrokerTests.Clock();
        using var certificate = new CertificateRequest("CN=result-connection", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddDays(1));
        var enrollment = new ComputeProviderEnrollment(f.Result.OrganizationId, f.Result.NodeId, f.Result.ProviderId,
            new("control", Convert.ToBase64String(f.Key.ExportSubjectPublicKeyInfo())));
        var config = new ComputeProviderConfiguration(1, enrollment, new("key-1", Convert.ToBase64String(f.Key.ExportSubjectPublicKeyInfo())),
            "https://core.example", Path.GetTempPath(), Path.GetTempPath(), new(10, new(40, 81920, 409600)), certificate.Thumbprint, StoreLocation.LocalMachine);
        using var connection = new HyperVResultDeliveryConnection(config, clock, (_, _) => new X509Certificate2(certificate),
            origin => new(origin, new Handler(async (request, token) =>
            {
                var envelope = JsonSerializer.Deserialize<SignedComputeProviderResult>(await request.Content!.ReadAsStringAsync(token), ComputeProtocol.Json)!;
                var applied = await f.Reconciler.ApplyAsync(envelope, token);
                var ack = new ComputeResultAcknowledgement(f.Result.OperationId, f.Result.Sequence, ComputeProtocol.Digest(envelope.PayloadJson), applied);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(ack, ComputeProtocol.Json), Encoding.UTF8, "application/json") };
            })));
        var row = new ComputeResultOutboxEntry(f.Result, ComputeProtocol.Digest(JsonSerializer.Serialize(f.Result, ComputeProtocol.Json)));
        Assert.True((await connection.DeliverAsync(row, default)).Applied);
        Assert.False((await connection.DeliverAsync(row, default)).Applied);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
