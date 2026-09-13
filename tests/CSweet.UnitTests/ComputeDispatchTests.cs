using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ComputeDispatchTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new ComputeBrokerTests.Clock().GetUtcNow();
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Signer : IComputeDispatchSigner, IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public List<ComputeDispatchAuthorization> Claims { get; } = [];
        public Task<ComputeSigningIdentity> GetIdentityAsync(CancellationToken token) =>
            Task.FromResult(new ComputeSigningIdentity("test-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        public Task<SignedComputeDispatch> SignAsync(ComputeDispatchAuthorization authorization, CancellationToken token)
        {
            lock (key)
            {
            Claims.Add(authorization);
            var json = JsonSerializer.Serialize(authorization, ComputeBroker.Json);
            return Task.FromResult(new SignedComputeDispatch("test-key", json,
                Convert.ToBase64String(key.SignData(ComputeDispatchSigner.Payload(json), HashAlgorithmName.SHA256))));
            }
        }
        public void Dispose() => key.Dispose();
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public ComputeBrokerTests.Fixture Broker { get; }
        public Clock Time { get; } = new();
        public Signer Signing { get; } = new();
        public Guid OperationId { get; private set; }
        public Fixture(DbContextOptions<CSweetDbContext>? options = null) { Broker = new(options); }
        public ComputeDispatchAuthorizer Authorizer(CSweetDbContext? db = null)
        {
            db ??= Broker.Db;
            var registry = new ComputeRegistry(db, Time, new AuditExecutionContextAccessor());
            return new(db, new ComputeBroker(db, registry, Time, Broker.Ledger), Signing, Time);
        }
        public async Task InitializeAsync()
        {
            await Broker.SeedAsync(); await Broker.Send();
            var environment = await Broker.Db.ComputeEnvironments.SingleAsync();
            var registry = new ComputeRegistry(Broker.Db, Time, new AuditExecutionContextAccessor());
            await registry.PutNodeAsync(Broker.Organization, environment.ProviderNodeId!.Value, 0, "Test node", environment.ProviderId!, "test-key",
                (await Signing.GetIdentityAsync(default)).PublicKeyBase64, true, default);
            var template = await registry.PutTemplateAsync(Broker.Organization,
                new("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), []), 0, default);
            await registry.PutPlacementAsync(Broker.Organization, template.Id, environment.ProviderNodeId.Value, 0, true, default);
            OperationId = (await Broker.Db.ComputeOperations.SingleAsync()).Id;
        }
        public async ValueTask DisposeAsync() { Signing.Dispose(); await Broker.DisposeAsync(); }
    }

    [Fact]
    public async Task Claim_commits_short_lived_authority_before_return_and_active_lease_deduplicates()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var authorizer = f.Authorizer();
        var packet = await authorizer.ClaimAsync(f.OperationId, default);
        Assert.NotNull(packet);
        var claim = Assert.Single(f.Signing.Claims);
        Assert.Equal(ComputeDispatchMode.Execute, claim.Mode);
        Assert.Equal(f.OperationId, claim.OperationId);
        Assert.Single(claim.Grants);
        Assert.Equal(f.Time.Now.AddMinutes(1), claim.ExpiresAt);
        Assert.NotNull(packet.Template);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String((await f.Signing.GetIdentityAsync(default)).PublicKeyBase64), out _);
        Assert.True(key.VerifyData(ComputeDispatchSigner.Payload(packet.Authorization.PayloadJson),
            Convert.FromBase64String(packet.Authorization.SignatureBase64), HashAlgorithmName.SHA256));
        var operation = await f.Broker.Db.ComputeOperations.SingleAsync();
        Assert.Equal(claim.DispatchId, operation.DispatchLeaseId);
        Assert.Equal("Dispatching", operation.Status);
        Assert.Equal(1, operation.Attempts);
        Assert.Null(await authorizer.ClaimAsync(f.OperationId, default));
        Assert.Single(f.Signing.Claims);
    }

    [Fact]
    public async Task Lost_response_recovers_by_observation_after_restart_even_if_agent_grant_is_revoked()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Authorizer().ClaimAsync(f.OperationId, default);
        var grant = await f.Broker.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Provision);
        grant.RevokedAt = f.Time.Now; await f.Broker.Db.SaveChangesAsync();
        f.Time.Now = f.Time.Now.AddSeconds(61);
        await using var restarted = new CSweetDbContext(f.Broker.Options);
        var recovered = await f.Authorizer(restarted).ClaimAsync(f.OperationId, default);
        Assert.NotNull(recovered);
        var claim = f.Signing.Claims.Last();
        Assert.Equal(ComputeDispatchMode.Observe, claim.Mode);
        Assert.Empty(claim.Grants);
        Assert.Null(recovered.Template);
        Assert.Equal(f.Signing.Claims[0].OperationId, claim.OperationId);
        Assert.Equal(f.Signing.Claims[0].Generation, claim.Generation);
        Assert.True((await restarted.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("revision")]
    [InlineData("actor")]
    [InlineData("template")]
    [InlineData("node")]
    [InlineData("expired")]
    public async Task Changed_authority_blocks_before_any_signed_dispatch(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var grant = await f.Broker.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Provision);
        if (scenario == "revoked") grant.RevokedAt = f.Time.Now;
        if (scenario == "revision") grant.Revision++;
        if (scenario == "actor") (await f.Broker.Db.CoreOrganizationUsers.SingleAsync()).ArchivedAt = f.Time.Now;
        if (scenario == "template") (await f.Broker.Db.ComputeTemplates.SingleAsync()).Enabled = false;
        if (scenario == "node") (await f.Broker.Db.ComputeNodes.SingleAsync()).Enabled = false;
        if (scenario == "expired") f.Time.Now = f.Time.Now.AddMinutes(11);
        await f.Broker.Db.SaveChangesAsync();
        Assert.Null(await f.Authorizer().ClaimAsync(f.OperationId, default));
        Assert.Empty(f.Signing.Claims);
        Assert.Equal("Blocked", (await f.Broker.Db.ComputeOperations.SingleAsync()).Status);
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }

    [Fact]
    public async Task Missing_operator_signing_configuration_fails_closed()
    {
        var signer = new ComputeDispatchSigner(Options.Create(new ComputeSigningOptions()), new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => signer.GetIdentityAsync(default));
    }
}
