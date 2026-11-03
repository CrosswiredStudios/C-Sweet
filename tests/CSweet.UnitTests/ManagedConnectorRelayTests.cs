using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.ConnectionService.Protocol;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ManagedConnectorRelayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EnterpriseReadUsesBusinessTenantAndNativeResourceWithoutLocalProviderCredentials()
    {
        await using var f = await PluginAccountAuthorityTests.Fixture.Create(); var gateway = new Gateway(); var secrets = Context(f.Legacy, gateway);
        var relay = Relay(f.Legacy, secrets, gateway); var direct = new ForbiddenTransport();
        var executor = new ConnectorReadExecutor(f.Legacy.Db, f.Legacy.Service, direct, secrets, new TestAuditEventWriter(), relay);
        for (var i = 0; i < 2; i++) Assert.Equal("value", (await executor.ExecuteAsync(f.Organization, f.Legacy.Requester.Id,
            ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("search"), "once", default)).GetProperty("data").GetString());
        Assert.Equal(1, gateway.Reads); Assert.Equal(f.Organization.ToString("D"), gateway.LastRead!.TenantId);
        Assert.Equal(f.Resource.ExternalResourceId, gateway.LastRead.ResourceId); Assert.Equal("enterprise", f.Legacy.Connector.BusinessId);
        Assert.Null(f.Connection.BoundResourceId); Assert.Equal("search", gateway.LastRead.Input.GetProperty("search").GetString());
        Assert.Equal(0, direct.Calls);
    }

    [Fact]
    public async Task ChangedNativeBusinessGrantWithholdsAServiceResult()
    {
        await using var f = await PluginAccountAuthorityTests.Fixture.Create(); var gateway = new Gateway(); var secrets = Context(f.Legacy, gateway);
        gateway.AfterRead = async () => { f.Grant.RevokedAt = DateTimeOffset.UtcNow; await f.Legacy.Db.SaveChangesAsync(); };
        var executor = new ConnectorReadExecutor(f.Legacy.Db, f.Legacy.Service, new ForbiddenTransport(), secrets, new TestAuditEventWriter(), Relay(f.Legacy, secrets, gateway));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(f.Organization, f.Legacy.Requester.Id,
            ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("search"), "once", default));
        Assert.Equal(1, gateway.Reads); Assert.Null((await f.Legacy.Db.ConnectorExecutions.SingleAsync()).ResultJson);
    }

    [Theory]
    [InlineData("missing-context")]
    [InlineData("origin-changed")]
    [InlineData("old-plan")]
    [InlineData("wrong-receipt")]
    public async Task MissingProvenanceNeverFallsBackToLocalTokens(string change)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create(); var gateway = new Gateway(); var secrets = Context(f, gateway);
        var relay = Relay(f, secrets, gateway); var record = await f.Prepare("search"); record.Status = "Executing"; await f.Db.SaveChangesAsync();
        var frozen = JsonSerializer.Deserialize<FrozenConnectorPlan>(record.PlanJson, Json)!;
        if (change == "missing-context") secrets.Values.Clear();
        if (change == "origin-changed") gateway.Origin = "https://other.example";
        if (change == "old-plan") frozen = frozen with { OriginalInput = null };
        if (change == "wrong-receipt") gateway.WrongReceipt = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            Assert.True(await relay.IsManagedAsync(f.Connector.Id, f.Connection.Id, default));
            await relay.ReadAsync(frozen, record.PlanHash, _ => Task.CompletedTask, default);
        });
        Assert.Equal(change == "wrong-receipt" ? 1 : 0, gateway.Reads);
    }

    [Fact]
    public async Task ManagedMediaKeepsExistingHumanApprovalAttachmentIntegrityAndDurableWorker()
    {
        await using var f = await ConnectorMediaTransferTests.Fixture.Create(native: true); var gateway = new Gateway();
        var context = JsonSerializer.Serialize(new ManagedAccountAttempt(Guid.NewGuid(), gateway.RemoteConnection, "synthetic-verifier", gateway.Origin), Json);
        f.Provider.Secrets[$"managed.context.{f.Inner.Connection.Id:N}"] = context;
        var relay = new ManagedConnectorRelay(f.Inner.Db, f.Provider, gateway, f.Inner.Service, f.Approval);
        var frozen = JsonSerializer.Deserialize<FrozenConnectorPlan>(f.Plan.PlanJson, Json)!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => relay.MediaAsync(frozen, ConnectorMediaStep.Begin, null, 0, default, 0, _ => Task.CompletedTask, default));
        Assert.Equal(0, gateway.Begins); await f.Approve();
        var transport = new ConnectorMediaTransport(f.Inner.Db, new ForbiddenTokens(), relay);
        var worker = new ConnectorMediaTransferService(f.Inner.Db, f.Approval, transport, new ForbiddenTransport(), f.Provider, f.Provider, f.Provider, relay);
        await worker.StartAsync(f.Plan, default); Assert.Equal("Executing", f.Plan.Status);
        var saved = f.Provider.Secrets[$"response.media-session.{f.Plan.Id:N}"];
        Assert.True(ManagedMediaHandle.IsHandle(saved)); Assert.DoesNotContain("https://", saved);
        Assert.Equal(f.Inner.Organization.ToString("D"), gateway.LastMedia!.Operation.TenantId);
        Assert.Equal("workspace-one", gateway.LastMedia.Operation.ResourceId); Assert.NotNull(frozen.AccountAuthority);
        Assert.Equal(f.Plan.ApprovalId!.Value.ToString("D"), gateway.LastMedia.ApprovalReference);
        Assert.Equal(f.Provider.Asset.Sha256, gateway.LastMedia.AssetSha256);
        await f.MakeDue(); Assert.True(await worker.ProcessNextAsync(default));
        await f.MakeDue(); Assert.True(await worker.ProcessNextAsync(default));
        Assert.Equal("Completed", f.Plan.Status); Assert.Equal(1, gateway.Begins); Assert.Equal(1, gateway.Chunks);
        Assert.Equal(f.Provider.Bytes, gateway.LastChunk); Assert.DoesNotContain(f.Provider.Secrets.Keys, x => x.StartsWith("response.media-session.", StringComparison.Ordinal));
        Assert.Empty(f.Provider.Steps); // No direct-provider transport/token path was used.
    }

    [Fact]
    public async Task RevokedMediaSourceQueuesLocalCancellationWithoutDeletingRemoteContent()
    {
        await using var f = await ConnectorMediaTransferTests.Fixture.Create(); var gateway = new Gateway();
        f.Provider.Secrets[$"managed.context.{f.Inner.Connection.Id:N}"] = JsonSerializer.Serialize(new ManagedAccountAttempt(Guid.NewGuid(), gateway.RemoteConnection, "verifier", gateway.Origin), Json);
        var relay = new ManagedConnectorRelay(f.Inner.Db, f.Provider, gateway, f.Inner.Service, f.Approval);
        await f.Approve(); var worker = new ConnectorMediaTransferService(f.Inner.Db, f.Approval,
            new ConnectorMediaTransport(f.Inner.Db, new ForbiddenTokens(), relay), new ForbiddenTransport(), f.Provider, f.Provider, f.Provider, relay);
        await worker.StartAsync(f.Plan, default);
        f.Inner.Connection.Status = PluginConnectionStatus.Revoked; await f.Inner.Db.SaveChangesAsync();
        await f.MakeDue(); await worker.ProcessNextAsync(default); Assert.Equal("Indeterminate", f.Plan.Status);
        Assert.NotNull(f.Job.AvailableAt); await f.MakeDue(); await worker.ProcessNextAsync(default);
        Assert.Equal(1, gateway.Cancellations); Assert.Equal(0, gateway.Chunks);
        Assert.DoesNotContain(f.Provider.Secrets.Keys, x => x.StartsWith("response.media-session.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectAndPendingDeletionRemoveAccountContextButMediaWorkerStillFinishesCancellation(bool interrupted)
    {
        await using var f = await ConnectorMediaTransferTests.Fixture.Create(native: true); var gateway = new Gateway();
        var db = f.Inner.Db; var connector = f.Inner.Connector.Id; var connection = f.Inner.Connection.Id;
        var group = await db.PluginAccountGroups.SingleAsync(); var admin = Guid.NewGuid();
        var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = AuthenticationService.AdministratorRole };
        db.Roles.Add(role); db.Users.Add(new ApplicationUser { Id = admin, UserName = "synthetic-admin" });
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = admin, RoleId = role.Id });
        var setup = new PluginAccountSetupSession { Id = Guid.NewGuid(), PluginInstallationId = connector, ConnectionId = connection,
            AccountGroupId = group.Id, Status = "Ready", CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        db.PluginAccountSetupSessions.Add(setup);
        f.Provider.Secrets[$"managed.context.{connection:N}"] = JsonSerializer.Serialize(
            new ManagedAccountAttempt(gateway.RemoteSession, gateway.RemoteConnection, "synthetic-verifier", gateway.Origin), Json);
        await db.SaveChangesAsync(); await f.Approve();
        var relay = new ManagedConnectorRelay(db, f.Provider, gateway, f.Inner.Service, f.Approval);
        var worker = new ConnectorMediaTransferService(db, f.Approval, new ConnectorMediaTransport(db, new ForbiddenTokens(), relay),
            new ForbiddenTransport(), f.Provider, f.Provider, f.Provider, relay);
        await worker.StartAsync(f.Plan, default);
        var saved = f.Provider.Secrets[$"response.media-session.{f.Plan.Id:N}"];
        using var http = new PluginAccountFlowTests.IdentityHttp();
        var accounts = new PluginAccountService(db, f.Provider, new EphemeralDataProtectionProvider(), http,
            new PluginAccountFlowTests.Profiles(), new ForbiddenTransport(), Options.Create(new PluginConnectionOptions()), f.Provider, managed: gateway);
        var impact = await accounts.GetDisconnectImpactAsync(admin, connector, group.Id, default);
        await accounts.DisconnectAsync(admin, connector, group.Id, new(impact.AccountRevision, impact.ReviewHash), default);
        await accounts.ProcessPendingAsync(default);
        Assert.Equal(1, gateway.AttemptCancellations); Assert.Equal(setup.Id, gateway.CancelledAttempt);
        Assert.Equal("RevokedDataDeletionPending", (await db.PluginAccountRevocationJobs.SingleAsync()).Status);
        Assert.False(f.Provider.Secrets.ContainsKey($"managed.context.{connection:N}"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => relay.IsManagedAsync(connector, connection, default));
        Assert.Equal("Erased", f.Plan.Status); Assert.Equal("{}", f.Plan.PlanJson);
        Assert.Equal(saved, f.Provider.Secrets[$"response.media-session.{f.Plan.Id:N}"]);
        Assert.Contains(await db.PluginAccountDataCopies.ToArrayAsync(), x => x.Store == "external:managed-service-backups" && x.DeletedAt is null);
        // A mode/origin change affects new execution, never the original protected cleanup destination.
        gateway.Origin = "https://new-service.example";
        gateway.FailCancellationOnce = interrupted;
        await f.MakeDue(); Assert.True(await worker.ProcessNextAsync(default));
        if (interrupted)
        {
            Assert.NotNull(f.Job.AvailableAt); Assert.Equal(saved, f.Provider.Secrets[$"response.media-session.{f.Plan.Id:N}"]);
            Assert.Equal(0, gateway.Cancellations);
            await f.MakeDue(); Assert.True(await worker.ProcessNextAsync(default));
        }
        Assert.Equal(ConnectorMediaTransferService.FinishedKind, f.Job.Kind);
        await f.MakeDue(); Assert.True(await worker.ProcessNextAsync(default));
        Assert.Equal(1, gateway.Cancellations); Assert.Equal("https://service.example", gateway.LastCancellationOrigin);
        Assert.Equal(0, gateway.Chunks); Assert.Equal(1, gateway.Begins); Assert.Null(f.Job.AvailableAt);
        Assert.False(f.Provider.Secrets.ContainsKey($"response.media-session.{f.Plan.Id:N}"));
        Assert.Equal(PluginAccountState.Disconnecting, group.State); // Outstanding backups remain independently accountable.
        Assert.False(await worker.ProcessNextAsync(default));
    }

    [Theory]
    [InlineData("connector")]
    [InlineData("connection")]
    [InlineData("malformed")]
    [InlineData("legacy")]
    [InlineData("oversized")]
    public async Task CleanupHandleCannotBeReboundOrDowngraded(string change)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create(); var gateway = new Gateway();
        var relay = Relay(f, new Secrets(), gateway);
        var connector = f.Connector.Id; var connection = f.Connection.Id;
        var saved = ManagedMediaHandle.Format(gateway.Transfer, new string('a', 64), connector, connection, gateway.Origin);
        if (change == "connector") connector = Guid.NewGuid();
        if (change == "connection") connection = Guid.NewGuid();
        if (change == "malformed") saved += ":untrusted";
        if (change == "legacy") saved = "csweet-managed-media:v1:" + gateway.Transfer.ToString("N") + ":" + new string('a', 64);
        if (change == "oversized") saved += new string('x', 2048);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => relay.CancelAsync(connector, connection, saved, default));
        Assert.Equal(0, gateway.Cancellations);
    }

    [Theory]
    [InlineData("http://service.example")]
    [InlineData("https://service.example/path")]
    [InlineData("https://service.example/")]
    [InlineData("https://user@service.example")]
    [InlineData("https://service.example?destination=other")]
    public void CleanupHandleRequiresExactCanonicalHttpsOrigin(string origin) => Assert.Throws<UnauthorizedAccessException>(() =>
        ManagedMediaHandle.Format(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), Guid.NewGuid(), origin));

    private static ManagedConnectorRelay Relay(ConnectorPlanServiceTests.Fixture f, IPluginSecretStore secrets, Gateway gateway) =>
        new(f.Db, secrets, gateway, f.Service, new(f.Db, f.Service, new TestAuditEventWriter()));
    private static Secrets Context(ConnectorPlanServiceTests.Fixture f, Gateway gateway)
    {
        var secrets = new Secrets(); secrets.Values[$"managed.context.{f.Connection.Id:N}"] = JsonSerializer.Serialize(
            new ManagedAccountAttempt(Guid.NewGuid(), gateway.RemoteConnection, "synthetic-verifier", gateway.Origin), Json); return secrets;
    }
    private sealed class Secrets : IPluginSecretStore
    {
        public readonly Dictionary<string, string> Values = [];
        public Task<string?> GetAsync(Guid id, string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(Guid id, string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(Guid id, string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }
    private sealed class ForbiddenTokens : IPluginOAuthTokenBroker
    { public Task<string?> GetAccessTokenAsync(Guid id, PluginConnection connection, CancellationToken ct = default) => throw new InvalidOperationException("No local OAuth credential path."); }
    private sealed class ForbiddenTransport : IConnectorHttpTransport
    {
        public int Calls;
        public Task<ConnectorProviderResponse> SendAsync(Guid connector, Guid connection, ConnectorPreparedRequest request, Func<CancellationToken, Task> revalidate, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No direct provider requests."); }
    }
    private sealed class Gateway : IManagedPluginConnectionGateway
    {
        public string Origin = "https://service.example"; public readonly Guid RemoteConnection = Guid.NewGuid(), Transfer = Guid.NewGuid(), RemoteSession = Guid.NewGuid();
        public int AttemptCancellations; public Guid CancelledAttempt; public string? LastCancellationOrigin; public bool FailCancellationOnce;
        public int Reads, Begins, Chunks, Cancellations; public bool WrongReceipt; public Func<Task>? AfterRead;
        public ManagedReadRequest? LastRead; public ManagedMediaRequest? LastMedia; public byte[]? LastChunk;
        public bool UsesManaged(string profile) => true; public string ReturnUri => "https://app.example/return";
        public Task<string> BoundServiceOrigin(CancellationToken ct) => Task.FromResult(Origin);
        public async Task<ManagedReadResult> ReadOperation(Guid connection, ManagedReadRequest request, string origin, CancellationToken ct)
        {
            Assert.Equal(RemoteConnection, connection); Reads++; LastRead = request; if (AfterRead is not null) await AfterRead();
            return new(connection, 1, WrongReceipt ? new string('b', 64) : ManagedExecutionBinding.Hash(connection, request), JsonSerializer.SerializeToElement(new { data = "value" }));
        }
        public Task<ManagedMediaResult> BeginMedia(Guid connection, ManagedMediaRequest request, string origin, CancellationToken ct)
        { Begins++; LastMedia = request; return Task.FromResult(new ManagedMediaResult(Transfer, request.BindingHash(connection), "Ready", 201, 0, 1)); }
        public Task<ManagedMediaResult> ProbeMedia(Guid transfer, string binding, string origin, CancellationToken ct) => Task.FromResult(new ManagedMediaResult(Transfer, binding, "Ready", 308, 0, 2));
        public Task<ManagedMediaResult> MediaChunk(Guid transfer, string binding, long offset, ReadOnlyMemory<byte> chunk, string origin, CancellationToken ct)
        { Chunks++; LastChunk = chunk.ToArray(); return Task.FromResult(new ManagedMediaResult(Transfer, binding, "Completed", 200, LastMedia!.SizeBytes, 3, JsonSerializer.SerializeToElement(new { data = "value" }))); }
        public Task<ManagedMediaResult> CancelMedia(Guid transfer, string binding, string origin, CancellationToken ct)
        {
            Assert.Equal(Transfer, transfer);
            if (FailCancellationOnce) { FailCancellationOnce = false; throw new HttpRequestException(); }
            Cancellations++; LastCancellationOrigin = origin; return Task.FromResult(new ManagedMediaResult(Transfer, binding, "Cancelled", 0, 0, 4));
        }
        public Task<ProfileAvailability> Availability(string d, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConnectionAttempt> Begin(BeginConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> Cancel(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> CancelAttempt(Guid id, CancellationToken ct, string? expectedServiceOrigin = null)
        { Assert.Equal(Origin, expectedServiceOrigin); AttemptCancellations++; CancelledAttempt = id; return Task.FromResult(new ConnectionReceipt(RemoteSession, RemoteConnection, "Cancelled", null, [])); }
        public Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> Redeem(RedeemConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> Recover(Guid id, RecoverConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionInventory> Discover(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionSnapshot> Disconnect(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionSnapshot> Snapshot(Guid id, CancellationToken ct, string? expectedServiceOrigin = null)
        { Assert.Equal(RemoteConnection, id); Assert.Equal(Origin, expectedServiceOrigin); return Task.FromResult(new ConnectionSnapshot(id, "Revoked", 4, [])); }
    }
}
