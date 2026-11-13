using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class ComputeWorkRecoveryHttpTests
{
    [Fact]
    public async Task Real_https_recovers_expired_observation_and_reconciles_signed_result_without_inventing_teardown()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        var clock = f.Provider.Journal.Core.Time;
        var broker = f.Provider.Journal.Core.Broker;
        var work = new ComputeProviderWorkService(f.Db, new(f.Trust, clock), f.Provider.Journal.Core.Authorizer(), broker.Ledger, clock);
        var reconciler = new ComputeResultReconciler(f.Db, new(f.Trust, clock), clock, broker.Ledger);
        await using var server = new ComputeMaintenanceHttpTests.Server(f, services =>
        { services.AddSingleton(work); services.AddSingleton(reconciler); });
        await server.StartAsync();
        using var certificate = new CertificateRequest("CN=compute-network-recovery", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        using var client = new ComputeWorkHttpClient(server.Https,
            new(f.Provider.Journal.Enrollment, certificate, "node-key", clock), f.Provider.Verifier, server.Handler());
        var operation = Assert.Single((await client.DiscoverAsync(null, default)).OperationIds);
        var original = Assert.Single(await f.Provider.Journal.Journal().ListResultsAsync(100, default));
        Assert.Equal(original.Result.OperationId, operation); Assert.True(original.Result.ExpiresAt <= clock.Now);
        var executor = f.Provider.Executor();
        var recovery = await new ComputeObservationRecovery(f.Provider.Journal.Journal(), f.Provider.Verifier, clock).RecoverAsync(null,
            client.ClaimAsync, async (packet, token) => { await executor.ExecuteAsync(packet, token); }, default);
        Assert.Equal(1, recovery.Observed);
        var row = Assert.Single(await f.Provider.Journal.Journal().ListResultsAsync(100, default));
        Assert.Equal(original.Result.Sequence + 1, row.Result.Sequence);
        Assert.Equal(ComputeLifecycleState.Failed, row.Result.State); Assert.False(row.Result.TeardownConfirmed);
        var signer = new ComputeProviderResultSigner(f.Provider.Journal.Enrollment, certificate, "node-key", clock);
        using var results = new ComputeResultHttpClient(server.Https, server.Handler());
        var signed = signer.Sign(row.Result);
        var acknowledgement = await results.DeliverAsync(signed, default);
        Assert.True(acknowledgement.Applied);
        Assert.False((await results.DeliverAsync(signed, default)).Applied);
        // Simulate losing the original acknowledgement until its evidence has expired.
        clock.Now = row.Result.ExpiresAt.AddMinutes(1);
        var receipt = await client.ReadResultReceiptAsync(row, default);
        Assert.NotNull(receipt); Assert.False(receipt.Applied); Assert.Equal(row.Digest, receipt.PayloadDigest);
        var receiptRecovery = await new ComputeObservationRecovery(f.Provider.Journal.Journal(), f.Provider.Verifier, clock).RecoverAsync(null,
            (_, _) => throw new InvalidOperationException("Recorded evidence needs no fresh claim."),
            (_, _) => throw new InvalidOperationException("Recorded evidence needs no fresh observation."), default, client.ReadResultReceiptAsync);
        Assert.Equal(1, receiptRecovery.Acknowledged); Assert.Equal(0, receiptRecovery.Observed);
        Assert.Empty(await f.Provider.Journal.Journal().ListResultsAsync(100, default));
        Assert.True((await f.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
        Assert.Equal(ComputeLifecycleState.Failed, (await f.Db.ComputeEnvironments.SingleAsync()).State);
        Assert.False(server.SawCookie); Assert.Equal(0, server.Captures);
    }

    [Fact]
    public async Task Real_https_work_client_refuses_redirect_and_revoked_node_has_no_claim_effect()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        var clock = f.Provider.Journal.Core.Time;
        var service = new ComputeProviderWorkService(f.Db, new(f.Trust, clock), f.Provider.Journal.Core.Authorizer(), f.Provider.Journal.Core.Broker.Ledger, clock);
        await using var server = new ComputeMaintenanceHttpTests.Server(f, services => services.AddSingleton(service));
        await server.StartAsync();
        using var certificate = new CertificateRequest("CN=compute-network-rejection", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        using var client = new ComputeWorkHttpClient(server.Https,
            new(f.Provider.Journal.Enrollment, certificate, "node-key", clock), f.Provider.Verifier, server.Handler());
        var attempts = (await f.Db.ComputeOperations.SingleAsync()).Attempts;
        server.Mode = "redirect";
        await Assert.ThrowsAsync<IOException>(() => client.DiscoverAsync(null, default)); Assert.Equal(0, server.Captures);
        server.Mode = "normal"; f.Trust.Revoked = true;
        await Assert.ThrowsAsync<IOException>(() => client.ClaimAsync(f.Provider.Claim.OperationId, default));
        Assert.Equal(attempts, (await f.Db.ComputeOperations.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Evidence_parts_are_signed_with_their_digest_retried_when_throttled_and_detect_an_older_core()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        var clock = f.Provider.Journal.Core.Time;
        using var certificate = new CertificateRequest("CN=compute-evidence-parts", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        var operation = Guid.NewGuid(); var dispatch = Guid.NewGuid(); var content = new byte[1000]; RandomNumberGenerator.Fill(content);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var responses = new Queue<System.Net.HttpStatusCode>([System.Net.HttpStatusCode.TooManyRequests, System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.NotFound]);
        var seen = new List<ComputeEvidencePartUpload>();
        var handler = new Respond(async request =>
        {
            Assert.Equal(ComputeWorkTransport.EvidencePartPath, request.RequestUri!.AbsolutePath);
            seen.Add(System.Text.Json.JsonSerializer.Deserialize<ComputeEvidencePartUpload>(await request.Content!.ReadAsByteArrayAsync(), ComputeProtocol.Json)!);
            var status = responses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (status == System.Net.HttpStatusCode.OK)
                response.Content = System.Net.Http.Json.JsonContent.Create(new ComputeEvidencePartReceipt(operation, dispatch, 0, sha), options: ComputeProtocol.Json);
            return response;
        });
        using var client = new ComputeWorkHttpClient(new Uri("https://core.test/"),
            new(f.Provider.Journal.Enrollment, certificate, "node-key", clock), f.Provider.Verifier, handler);
        Assert.True(await client.UploadEvidencePartAsync(operation, dispatch, 0, content, default)); // throttled once, then stored
        Assert.Equal(2, seen.Count);
        var proof = System.Text.Json.JsonSerializer.Deserialize<ComputeProviderWorkRequest>(seen[^1].Request.PayloadJson, ComputeProtocol.Json)!;
        Assert.Equal(sha, proof.InputSha256); Assert.Equal(0, proof.InputOffset); Assert.Equal(dispatch, proof.DispatchId); Assert.Equal(operation, proof.OperationId);
        Assert.False(await client.UploadEvidencePartAsync(operation, dispatch, 0, content, default)); // an older Core has no endpoint
    }

    private sealed class Respond(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
