using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeResultTests
{
    internal sealed class Trust(ComputeNodeVerificationKey key) : IComputeNodeTrust
    {
        public bool Revoked { get; set; }
        public Task<ComputeNodeVerificationKey?> ResolveAsync(Guid organizationId, Guid nodeId, string keyId, CancellationToken token) =>
            Task.FromResult(!Revoked && key.OrganizationId == organizationId && key.NodeId == nodeId && key.KeyId == keyId ? key : null);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public ComputeBrokerTests.Fixture Broker { get; }
        public Fixture(DbContextOptions<CSweet.Infrastructure.Persistence.CSweetDbContext>? options = null) { Broker = new(options); }
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public Trust Enrollment { get; private set; } = null!;
        public ComputeResultReconciler Reconciler { get; private set; } = null!;
        public ComputeProviderResult Result { get; private set; } = null!;
        public async Task InitializeAsync()
        {
            await Broker.SeedAsync(); await Broker.Send();
            var operation = await Broker.Db.ComputeOperations.SingleAsync(); operation.Status = "Dispatching";
            var environment = await Broker.Db.ComputeEnvironments.SingleAsync();
            Enrollment = new(new("key-1", Broker.Organization, environment.ProviderNodeId!.Value,
                environment.ProviderId!, Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo())));
            Reconciler = new(Broker.Db, new(Enrollment, new ComputeBrokerTests.Clock()), new ComputeBrokerTests.Clock(), Broker.Ledger);
            Result = new(operation.Id, environment.Id, Broker.Organization, Broker.Installation, environment.ProviderId!,
                environment.ProviderNodeId.Value, 1, InfrastructureActions.Provision, ComputeBroker.Digest(environment.SpecificationJson),
                1, "vm-1", ComputeLifecycleState.Ready, false, environment.CreatedAt, environment.CreatedAt.AddMinutes(2));
            await Broker.Db.SaveChangesAsync();
        }
        public SignedComputeProviderResult Sign(ComputeProviderResult? result = null, string? wrongPurpose = null)
        {
            var json = JsonSerializer.Serialize(result ?? Result, ComputeBroker.Json);
            var bytes = wrongPurpose is null ? ComputeProviderResultVerifier.Payload(json) : Encoding.UTF8.GetBytes(wrongPurpose + json);
            return new("key-1", json, Convert.ToBase64String(Key.SignData(bytes, HashAlgorithmName.SHA256)));
        }
        public async ValueTask DisposeAsync() { Key.Dispose(); await Broker.DisposeAsync(); }
    }

    [Fact]
    public async Task Signed_result_updates_state_atomically_and_duplicate_is_a_noop()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var envelope = f.Sign();
        Assert.True(await f.Reconciler.ApplyAsync(envelope, default));
        Assert.False(await f.Reconciler.ApplyAsync(envelope, default));
        var environment = await f.Broker.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(ComputeLifecycleState.Ready, environment.State);
        Assert.True(environment.HoldsReservation);
        Assert.Equal("vm-1", environment.ProviderResourceId);
        Assert.Equal("Completed", (await f.Broker.Db.ComputeOperations.SingleAsync()).Status);
        Assert.Equal(2, await f.Broker.Db.ComputeAuditOutbox.CountAsync());
        Assert.Equal(2, await f.Broker.Db.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.changed.v1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reconciler.ApplyAsync(f.Sign(f.Result with { ResourceId = "different-vm" }), default));
    }

    [Fact]
    public async Task Failure_and_old_generation_never_release_quota_but_current_destroy_confirmation_does()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        Assert.True(await f.Reconciler.ApplyAsync(f.Sign(f.Result with { State = ComputeLifecycleState.Failed, FailureCode = "unknown-outcome" }), default));
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
        Assert.Equal("Reconciling", (await f.Broker.Db.ComputeOperations.SingleAsync()).Status);
        await f.Broker.Broker.ChangeLifecycleAsync(f.Broker.Organization, f.Broker.Installation,
            new(f.Result.EnvironmentId, 1, InfrastructureActions.Destroy, "cleanup"), default);
        Assert.False(await f.Reconciler.ApplyAsync(f.Sign(f.Result with { Sequence = 2 }), default));
        var destroy = await f.Broker.Db.ComputeOperations.SingleAsync(x => x.Action == InfrastructureActions.Destroy);
        destroy.Status = "Dispatching"; await f.Broker.Db.SaveChangesAsync();
        Assert.True(await f.Reconciler.ApplyAsync(f.Sign(f.Result with { OperationId = destroy.Id, Generation = 2,
            Action = InfrastructureActions.Destroy, State = ComputeLifecycleState.Destroyed, TeardownConfirmed = true }), default));
        Assert.False((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }

    [Fact]
    public async Task Out_of_order_evidence_does_not_regress_progress()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        Assert.True(await f.Reconciler.ApplyAsync(f.Sign(f.Result with { Sequence = 2, State = ComputeLifecycleState.Bootstrapping }), default));
        Assert.False(await f.Reconciler.ApplyAsync(f.Sign(f.Result with { State = ComputeLifecycleState.Provisioning }), default));
        Assert.Equal(ComputeLifecycleState.Bootstrapping, (await f.Broker.Db.ComputeEnvironments.SingleAsync()).State);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("placement")]
    [InlineData("digest")]
    [InlineData("teardown")]
    [InlineData("undispatched")]
    public async Task Invalid_or_untrusted_evidence_cannot_mutate_compute(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var result = f.Result;
        if (scenario == "revoked") f.Enrollment.Revoked = true;
        if (scenario == "expired") result = result with { ExpiresAt = result.ObservedAt };
        if (scenario == "placement") result = result with { NodeId = Guid.NewGuid() };
        if (scenario == "digest") result = result with { SpecificationDigest = "sha256:" + new string('0', 64) };
        if (scenario == "teardown") result = result with { TeardownConfirmed = true };
        if (scenario == "undispatched")
        {
            (await f.Broker.Db.ComputeOperations.SingleAsync()).Status = "Pending"; await f.Broker.Db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reconciler.ApplyAsync(f.Sign(result,
            scenario == "purpose" ? "CSweet.WebHost.Assignment.v1\n" : null), default));
        Assert.Equal(ComputeLifecycleState.Requested, (await f.Broker.Db.ComputeEnvironments.SingleAsync()).State);
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
        Assert.Equal(1, await f.Broker.Db.ComputeAuditOutbox.CountAsync());
        Assert.Equal("Rejected", Assert.Single(f.Broker.Ledger.Events).Outcome);
    }
}
