using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.ConnectionService.Protocol;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

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
        public string Origin = "https://service.example"; public readonly Guid RemoteConnection = Guid.NewGuid(), Transfer = Guid.NewGuid();
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
        { Cancellations++; return Task.FromResult(new ManagedMediaResult(Transfer, binding, "Cancelled", 0, 0, 4)); }
        public Task<ProfileAvailability> Availability(string d, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConnectionAttempt> Begin(BeginConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> Cancel(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> CancelAttempt(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> Redeem(RedeemConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionReceipt> Recover(Guid id, RecoverConnection r, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionInventory> Discover(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionSnapshot> Disconnect(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
        public Task<ConnectionSnapshot> Snapshot(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => throw new NotSupportedException();
    }
}
