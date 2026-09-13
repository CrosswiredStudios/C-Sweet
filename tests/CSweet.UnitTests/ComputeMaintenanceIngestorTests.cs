using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceIngestorTests
{
    internal sealed class Fixture : IAsyncDisposable
    {
        public ComputeHyperVExecutorTests.Fixture Provider { get; } = new();
        public CSweetDbContext Db => Provider.Journal.Core.Broker.Db;
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private X509Certificate2 certificate = null!;
        private ServiceProvider services = null!;
        public EphemeralDataProtectionProvider Protection { get; } = new();
        public ComputeResultTests.Trust Trust { get; private set; } = null!;
        public ComputeMaintenanceSigner Signer { get; private set; } = null!;
        public ComputeMaintenanceIngestor Receiver { get; private set; } = null!;
        public ComputeMaintenanceEvent Evidence { get; private set; } = null!;
        public async Task InitializeAsync()
        {
            await Provider.InitializeAsync(); await Provider.Executor().ExecuteAsync(Provider.Packet, default);
            Provider.Journal.Core.Time.Now = Provider.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
            await Provider.Executor().EnforceExpiredLeaseAsync(Provider.Claim.EnvironmentId, default);
            var pending = await Provider.Journal.Journal().ListMaintenanceEventsAsync(100, default);
            Evidence = pending.Select(x => JsonSerializer.Deserialize<ComputeMaintenanceEvent>(x.EventJson, ComputeProtocol.Json)!)
                .Single(x => x.Phase == ComputeMaintenancePhase.Observed);
            certificate = new CertificateRequest("CN=compute-maintenance-test", Key, HashAlgorithmName.SHA256)
                .CreateSelfSigned(Provider.Claim.IssuedAt.AddDays(-1), Provider.Claim.IssuedAt.AddYears(1));
            Trust = new(new("node-key", Evidence.OrganizationId, Evidence.NodeId, Evidence.ProviderId,
                Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo())));
            Signer = new(certificate, "node-key", Provider.Journal.Core.Time);
            var collection = new ServiceCollection();
            collection.AddScoped(_ => new CSweetDbContext(Provider.Journal.Core.Broker.Options));
            services = collection.BuildServiceProvider();
            var writer = new AuditEventWriter(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), Protection);
            Receiver = new(Db, new(Trust, Provider.Journal.Core.Time), writer);
        }
        public SignedComputeMaintenanceDelivery Sign(ComputeMaintenanceEvent? evidence = null)
        {
            evidence ??= Evidence;
            var json = JsonSerializer.Serialize(evidence, ComputeProtocol.Json);
            return Signer.Sign(new(evidence.EventId, evidence.OccurredAt, json, ComputeProtocol.Digest(json)));
        }
        public async ValueTask DisposeAsync()
        { if (services is not null) await services.DisposeAsync(); certificate?.Dispose(); Key.Dispose(); await Provider.DisposeAsync(); }
    }

    [Fact]
    public async Task Historical_cleanup_evidence_remains_auditable_after_agent_disable_and_grant_revocation()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        foreach (var grant in await f.Db.ScopedActionGrants.ToListAsync()) grant.RevokedAt = f.Provider.Journal.Core.Time.Now;
        (await f.Db.AgentInstallations.SingleAsync()).IsEnabled = false;
        foreach (var actor in await f.Db.CoreOrganizationUsers.ToListAsync()) actor.IsActive = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal(f.Evidence.EventId, await f.Receiver.ApplyAsync(f.Sign(), default));
        Assert.Equal("compute.provider-maintenance.v1", (await f.Db.AuditEvents.SingleAsync()).EventType);
        Assert.True((await f.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }
    [Fact]
    public async Task Offline_evidence_resigned_after_reconnect_produces_one_sealed_record_without_changing_lifecycle()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        f.Provider.Journal.Core.Time.Now = f.Evidence.OccurredAt.AddDays(2);
        Assert.Equal(f.Evidence.EventId, await f.Receiver.ApplyAsync(f.Sign(), default));
        f.Provider.Journal.Core.Time.Now = f.Provider.Journal.Core.Time.Now.AddMinutes(1);
        Assert.Equal(f.Evidence.EventId, await f.Receiver.ApplyAsync(f.Sign(), default));
        var record = await f.Db.AuditEvents.SingleAsync();
        Assert.Equal(record.RecordHash, AuditIntegrity.ComputeRecordHash(record));
        Assert.Equal(record.RecordHash, f.Protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(record.IntegritySeal!));
        Assert.Contains(f.Evidence.ProvisionOperationId.ToString(), record.MetadataJson);
        Assert.Contains(f.Evidence.ProvisionGrants[0].GrantId.ToString(), record.MetadataJson);
        var environment = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(ComputeLifecycleState.Requested, environment.State);
        Assert.True(environment.HoldsReservation); Assert.Null(environment.TeardownConfirmedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Receiver.ApplyAsync(f.Sign(f.Evidence with { Attempt = 2 }), default));
        Assert.Equal(1, await f.Db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Provider_outbox_recovers_a_lost_core_acknowledgement_without_duplicate_ledger_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => new ComputeMaintenanceOutboxDispatcher(f.Provider.Journal.Journal()).DispatchAsync(async (entry, token) =>
        {
            await f.Receiver.ApplyAsync(f.Signer.Sign(entry), token);
            throw new IOException("Core committed before the transport lost its acknowledgement.");
        }, default));
        Assert.Equal(1, await f.Db.AuditEvents.CountAsync());
        await new ComputeMaintenanceOutboxDispatcher(f.Provider.Journal.Journal()).DispatchAsync(async (entry, token) =>
        { await f.Receiver.ApplyAsync(f.Signer.Sign(entry), token); }, default);
        Assert.Equal(2, await f.Db.AuditEvents.CountAsync());
        Assert.Empty(await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        Assert.True((await f.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("revoked")]
    [InlineData("grant")]
    [InlineData("installation")]
    [InlineData("action")]
    [InlineData("digest")]
    [InlineData("resource")]
    [InlineData("phase")]
    [InlineData("expired-delivery")]
    [InlineData("undispatched")]
    [InlineData("persistence")]
    public async Task Invalid_node_evidence_or_changed_provisioning_terms_are_rejected_and_audited(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var evidence = f.Evidence;
        if (scenario == "revoked") f.Trust.Revoked = true;
        if (scenario == "grant") evidence = evidence with { ProvisionGrants = [evidence.ProvisionGrants[0] with { Revision = 99 }] };
        if (scenario == "installation") evidence = evidence with { InstallationId = Guid.NewGuid() };
        if (scenario == "action") evidence = evidence with { Action = InfrastructureActions.Start };
        if (scenario == "digest") evidence = evidence with { SpecificationDigest = "sha256:" + new string('b', 64) };
        if (scenario == "phase") evidence = evidence with { Phase = ComputeMaintenancePhase.Requested };
        if (scenario == "persistence") evidence = evidence with { Persistence = ComputePersistence.Persistent, Action = InfrastructureActions.Stop };
        if (scenario == "resource") { (await f.Db.ComputeEnvironments.SingleAsync()).ProviderResourceId = "different-resource"; await f.Db.SaveChangesAsync(); }
        if (scenario == "undispatched") { (await f.Db.ComputeOperations.SingleAsync()).Attempts = 0; await f.Db.SaveChangesAsync(); }
        var envelope = f.Sign(evidence);
        if (scenario == "purpose") envelope = envelope with { SignatureBase64 = Convert.ToBase64String(f.Key.SignData(ComputeProtocol.ResultPayload(envelope.PayloadJson), HashAlgorithmName.SHA256)) };
        if (scenario == "expired-delivery") f.Provider.Journal.Core.Time.Now = f.Provider.Journal.Core.Time.Now.AddMinutes(3);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Receiver.ApplyAsync(envelope, default));
        var rejected = await f.Db.AuditEvents.SingleAsync();
        Assert.Equal("compute.provider-maintenance.rejected.v1", rejected.EventType);
        Assert.Null(rejected.MetadataJson);
        Assert.True((await f.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }
}
