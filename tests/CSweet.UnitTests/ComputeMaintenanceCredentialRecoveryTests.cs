using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceCredentialRecoveryTests
{
    [Fact]
    public async Task Repaired_access_to_the_same_enrolled_certificate_recovers_pending_evidence_without_worker_restart()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        var clock = f.Provider.Journal.Core.Time;
        using var certificate = Certificate(f, clock.Now.AddHours(1));
        var config = Configuration(f, certificate);
        var available = false; var opens = 0;
        var handler = new ReceiverHandler(f);
        using var connection = new HyperVMaintenanceDeliveryConnection(config, clock, (settings, time) =>
        {
            opens++;
            if (!available) throw new UnauthorizedAccessException("Key ACL has not yet been repaired.");
            return ComputeProviderConfigurationLoader.SelectSigningCertificate(settings, [certificate], time);
        }, (origin, signer) => new(origin, signer, handler));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var passes = new List<ComputeMaintenanceDeliveryPass>();
        var worker = new ComputeMaintenanceDeliveryWorker(f.Provider.Journal.Journal(), connection.DeliverAsync,
            TimeProvider.System, pass =>
            {
                passes.Add(pass);
                if (pass.ConsecutiveFailures > 0) available = true;
                else cancellation.Cancel();
            }, initialRetry: TimeSpan.FromMilliseconds(100), maximumRetry: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cancellation.Token));
        Assert.Equal(2, opens);
        Assert.Equal(new[] { 1, 0 }, passes.Select(x => x.ConsecutiveFailures));
        Assert.Equal(2, await f.Db.AuditEvents.CountAsync());
        Assert.Empty(await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        Assert.Single(await f.Provider.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Expiring_cached_credential_is_disposed_and_reselection_cannot_fall_back_to_another_key()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        var clock = f.Provider.Journal.Core.Time;
        using var certificate = Certificate(f, clock.Now.AddHours(1));
        var config = Configuration(f, certificate);
        var opens = 0;
        var handler = new ReceiverHandler(f);
        using var connection = new HyperVMaintenanceDeliveryConnection(config, clock, (settings, time) =>
        { opens++; return ComputeProviderConfigurationLoader.SelectSigningCertificate(settings, [certificate], time); },
            (origin, signer) => new(origin, signer, handler));
        var row = (await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(1, default))[0];
        await connection.DeliverAsync(row, default);
        clock.Now = clock.Now.AddMinutes(59);
        await Assert.ThrowsAsync<IOException>(() => connection.DeliverAsync(row, default));
        Assert.True(handler.IsDisposed);
        await Assert.ThrowsAsync<IOException>(() => connection.DeliverAsync(row, default));
        Assert.Equal(2, opens);
        Assert.Equal(1, await f.Db.AuditEvents.CountAsync());
        Assert.Equal(2, (await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(100, default)).Count);
    }

    private static X509Certificate2 Certificate(ComputeMaintenanceIngestorTests.Fixture f, DateTimeOffset until) =>
        new CertificateRequest("CN=provider-credential-recovery-test", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(f.Provider.Journal.Core.Time.Now.AddDays(-1), until);

    private static ComputeProviderConfiguration Configuration(ComputeMaintenanceIngestorTests.Fixture f, X509Certificate2 certificate) =>
        new(1, f.Provider.Journal.Enrollment, new("node-key", Convert.ToBase64String(f.Key.ExportSubjectPublicKeyInfo())),
            "https://core.example.test", Path.Combine(f.Provider.Journal.Root, "state"), f.Provider.Workloads, new(10, new(40, 81920, 409600)),
            certificate.Thumbprint, StoreLocation.LocalMachine);

    private sealed class ReceiverHandler(ComputeMaintenanceIngestorTests.Fixture fixture) : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var envelope = JsonSerializer.Deserialize<SignedComputeMaintenanceDelivery>(await request.Content!.ReadAsStringAsync(token), ComputeProtocol.Json)!;
            var id = await fixture.Receiver.ApplyAsync(envelope, token);
            var delivery = JsonSerializer.Deserialize<ComputeMaintenanceDelivery>(envelope.PayloadJson, ComputeProtocol.Json)!;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new ComputeMaintenanceAcknowledgement(id, delivery.EventDigest),
                    ComputeProtocol.Json), Encoding.UTF8, "application/json")
            };
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
}
