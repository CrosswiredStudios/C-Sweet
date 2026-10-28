using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ManagedPluginAccountFlowTests
{
    [Fact]
    public async Task ManagedConsentBindsHumanAndBrowserImportsResourcesWithoutLocalTokensOrAmbientGrants()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!);
        f.Managed = gateway;
        var binding = DeploymentProofCodec.Random();
        var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, binding);
        Assert.Equal(f.Admin.ToString("D"), gateway.Start!.ActorId); Assert.Equal(begin.SetupId, gateway.Start.LocalAttemptId);
        Assert.Equal("https://managed.example/connect/opaque", begin.AuthorizationUrl);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), DeploymentProofCodec.Random(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteManagedAsync(f.Manager, begin.SetupId, new string('h', 43), binding, default));
        var complete = await f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default);
        Assert.Equal(f.Id, complete.InstallationId); Assert.Equal(1, gateway.Redemptions);
        await f.Service.ImportAsync(begin.SetupId, default);
        var view = await f.Service.GetAsync(f.Admin, f.Id, default); var account = Assert.Single(view.Accounts);
        Assert.True(account.ProviderVerified); Assert.Single(account.Resources); Assert.Empty(account.BusinessGrants);
        Assert.Equal("Ready", Assert.Single(view.SetupSessions).Status);
        var connection = await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == Assert.Single(account.Authorizations).Id);
        Assert.Null(connection.ActiveCredentialAttemptId); Assert.Equal(0, f.Http.Exchanges);
        Assert.DoesNotContain(f.Secrets.Values.Keys, x => x.Contains("tokens", StringComparison.Ordinal));
        Assert.Null(await new PluginOAuthTokenBroker(f.Secrets, f.Inner.Db, f.Http, f.Profiles, gateway).GetAccessTokenAsync(f.Id, connection));
        Assert.Null(await new PluginOAuthTokenBroker(f.Secrets, f.Inner.Db, f.Http, f.Profiles).GetAccessTokenAsync(f.Id, connection));
        await f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default);
        Assert.Equal(1, gateway.Redemptions);
        var impact = await f.Service.GetDisconnectImpactAsync(f.Admin, f.Id, account.Id, default);
        await f.Service.DisconnectAsync(f.Admin, f.Id, account.Id, new(impact.AccountRevision, impact.ReviewHash), default);
        await f.Service.ProcessPendingAsync(default);
        Assert.True(gateway.Cancelled);
        Assert.Equal(PluginConnectionStatus.Revoked, (await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == connection.Id)).Status);
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(), x => x.Store == "external:managed-service-backups" && x.DeletedAt is null);
    }

    [Fact]
    public async Task ManagedReconnectImportsWithoutLocalTokensAndDurablyRetiresOnlyItsSupersededAuthority()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!); f.Managed = gateway;
        async Task<CSweet.Contracts.Plugins.BeginPluginAccountResponse> Connect(Guid? previous = null)
        {
            var binding = DeploymentProofCodec.Random();
            var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest, previous), default, binding);
            await f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default);
            await f.Service.ImportAsync(begin.SetupId, default); return begin;
        }
        await Connect(); var account = Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts);
        var previous = Assert.Single(account.Authorizations).Id;
        var next = await Connect(previous);
        Assert.Equal("Ready", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == next.SetupId)).Status);
        Assert.Equal(PluginConnectionStatus.Revoked, (await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == previous)).Status);
        await f.Service.ProcessPendingAsync(default);
        Assert.Equal(1, gateway.Retirements);
        Assert.Single(f.Secrets.Values.Keys, x => x.Contains("managed.context.", StringComparison.Ordinal));
        Assert.Equal(0, f.Http.Exchanges);
    }

    [Fact]
    public async Task AbandonedManagedAttemptsAreCancelledByLocalIdentityBeforeProofMaterialIsErased()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!); f.Managed = gateway;
        var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, DeploymentProofCodec.Random());
        var pending = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == begin.SetupId);
        pending.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await f.Inner.Db.SaveChangesAsync();
        await f.Service.ProcessPendingAsync(default);
        Assert.True(gateway.Cancelled); Assert.Equal(begin.SetupId, gateway.CancelledAttempt); Assert.Empty(f.Secrets.Values);
        Assert.Equal("Expired", pending.Status);
    }

    [Fact]
    public async Task ManagedToCustomerOwnedReconnectRetriesRetirementWithoutBlockingIndependentImports()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!);
        f.Managed = gateway; f.Transport.Subject = "provider-subject";
        var original = await ConnectManaged(f);
        var previous = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == original);
        gateway.ManagedMode = false;
        var replacement = await f.Begin(reconnect: previous.ConnectionId);
        await f.Complete(replacement);
        await f.Service.ImportAsync(replacement.SetupId, default);
        await f.Service.ImportAsync(replacement.SetupId, default);
        var completed = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == replacement.SetupId);
        Assert.Equal("Ready", completed.Status);
        var independent = await f.Begin(); await f.Complete(independent);
        gateway.CleanupUnavailable = true;
        await f.Service.ProcessPendingAsync(default);
        Assert.NotNull(completed.RetiredCredentialKey);
        Assert.Contains(f.Secrets.Values.Keys, x => x.EndsWith($"managed.context.{previous.ConnectionId:N}", StringComparison.Ordinal));
        var importing = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == independent.SetupId);
        Assert.True(importing.PagesImported > 0);
        gateway.CleanupUnavailable = false; gateway.RevocationStatus = "RevocationPending";
        await f.Service.ProcessPendingAsync(default);
        Assert.NotNull(completed.RetiredCredentialKey);
        Assert.Equal("Ready", importing.Status);
        gateway.RevocationStatus = "Revoked";
        await f.Service.ProcessPendingAsync(default);
        Assert.Null(completed.RetiredCredentialKey);
        Assert.Equal(original, gateway.CancelledAttempt);
        Assert.Equal(0, gateway.Retirements);
        Assert.DoesNotContain(f.Secrets.Values.Keys, x => x.Contains("managed.context.", StringComparison.Ordinal));
        Assert.Equal("Ready", importing.Status);
        var current = await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == completed.ConnectionId);
        Assert.NotNull(await f.Secrets.GetAsync(f.Id, PluginOAuthCredentialKeys.Generation(current.Id, current.ActiveCredentialAttemptId!.Value)));
    }

    [Fact]
    public async Task ManagedDisconnectAfterCustomerOwnedSwitchKeepsProofUntilProviderRevocationIsConfirmed()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!);
        f.Managed = gateway;
        var original = await ConnectManaged(f);
        var session = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == original);
        gateway.ManagedMode = false;
        var impact = await f.Service.GetDisconnectImpactAsync(f.Admin, f.Id, session.AccountGroupId, default);
        await f.Service.DisconnectAsync(f.Admin, f.Id, session.AccountGroupId, new(impact.AccountRevision, impact.ReviewHash), default);
        gateway.CleanupUnavailable = true;
        await f.Service.ProcessPendingAsync(default);
        var job = await f.Inner.Db.PluginAccountRevocationJobs.SingleAsync();
        Assert.Equal("Pending", job.Status);
        Assert.NotNull(await f.Secrets.GetAsync(f.Id, $"managed.context.{session.ConnectionId:N}"));
        gateway.CleanupUnavailable = false; job.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1); await f.Inner.Db.SaveChangesAsync();
        await f.Service.ProcessPendingAsync(default);
        Assert.Equal("RevokedDataDeletionPending", job.Status);
        Assert.Equal(original, gateway.CancelledAttempt);
        Assert.Null(await f.Secrets.GetAsync(f.Id, $"managed.context.{session.ConnectionId:N}"));
        Assert.Equal(0, f.Http.Exchanges);
    }

    private static async Task<Guid> ConnectManaged(PluginAccountFlowTests.Fixture f)
    {
        var binding = DeploymentProofCodec.Random();
        var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, binding);
        await f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default);
        await f.Service.ImportAsync(begin.SetupId, default);
        return begin.SetupId;
    }

    [Fact]
    public async Task ChangingPairedServiceCannotRedeemAnOldServicesHandoffEvenWithMatchingProfileMetadata()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!);
        f.Managed = gateway;
        var binding = DeploymentProofCodec.Random();
        var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, binding);
        gateway.Origin = "https://replacement.example";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default));
        Assert.Equal(0, gateway.Redemptions);
        Assert.Empty(await f.Inner.Db.PluginAccountResourceAuthorizations.ToArrayAsync());
    }

    [Fact]
    public async Task ManagedModeDoesNotFallBackToLocalSecretsOnServiceFailure()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); f.Managed = new Gateway("unused") { Unavailable = true };
        var view = await f.Service.GetAsync(f.Admin, f.Id, default);
        Assert.False(view.CanConnect); Assert.Equal("managed_connection_unavailable", view.ReadinessMessage);
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, DeploymentProofCodec.Random()));
        Assert.Equal(0, f.Http.Exchanges); Assert.Empty(f.Secrets.Values);
    }

    private sealed class Gateway(string profile) : IManagedPluginConnectionGateway
    {
        private Guid session = Guid.NewGuid(), connection = Guid.NewGuid();
        public BeginConnection? Start; public bool Unavailable, Cancelled, CleanupUnavailable; public int Redemptions;
        public bool ManagedMode = true;
        public string RevocationStatus = "Revoked";
        public string Origin = "https://managed.example";
        public int Retirements; public Guid CancelledAttempt;
        public bool UsesManaged(string id) => ManagedMode;
        public Task<string> BoundServiceOrigin(CancellationToken ct) => Task.FromResult(Origin);
        public string ReturnUri => "https://customer.example/settings/plugins/managed-return";
        public Task<ProfileAvailability> Availability(string digest, CancellationToken ct) => Unavailable ? Task.FromException<ProfileAvailability>(new HttpRequestException()) : Task.FromResult(new ProfileAvailability(profile, 1, new string('b', 64), "https://managed.example/oauth/callback/profile"));
        public Task<ConnectionAttempt> Begin(BeginConnection request, CancellationToken ct, string? expectedServiceOrigin = null) { Assert.Equal(Origin, expectedServiceOrigin); session = Guid.NewGuid(); connection = Guid.NewGuid(); Start = request; return Task.FromResult(new ConnectionAttempt(session, "Created", "https://managed.example/connect/opaque", DateTimeOffset.UtcNow.AddMinutes(10))); }
        public Task<ConnectionReceipt> Redeem(RedeemConnection request, CancellationToken ct, string? expectedServiceOrigin = null) { Assert.Equal(Origin, expectedServiceOrigin); Redemptions++; return Task.FromResult(new ConnectionReceipt(session, connection, "AuthorizedPendingDiscovery", null, ["base"])); }
        public Task<ConnectionReceipt> Recover(Guid id, RecoverConnection request, CancellationToken ct, string? expectedServiceOrigin = null) => Task.FromResult(new ConnectionReceipt(session, connection, "AuthorizedPendingDiscovery", null, ["base"]));
        public Task<ConnectionInventory> Discover(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) { Assert.Equal(Origin, expectedServiceOrigin); return Task.FromResult(new ConnectionInventory(connection, "Ready", 2, "provider-subject", "Managed account", "https://api.example.com/identity", [new("workspace-a", "Workspace A", null)], ["base"])); }
        public Task<ConnectionReceipt> Cancel(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) { Cancelled = true; return Task.FromResult(new ConnectionReceipt(session, connection, "Cancelled", null, [])); }
        public Task<ConnectionReceipt> CancelAttempt(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) { Assert.Equal("https://managed.example", expectedServiceOrigin); if (CleanupUnavailable) throw new HttpRequestException("Synthetic cleanup interruption"); CancelledAttempt = id; return Cancel(id, ct, expectedServiceOrigin); }
        public Task<ConnectionSnapshot> Supersede(Guid previous, Guid replacement, CancellationToken ct, string? expectedServiceOrigin = null) { Assert.Equal("https://managed.example", expectedServiceOrigin); Assert.NotEqual(previous, replacement); Retirements++; return Task.FromResult(new ConnectionSnapshot(previous, "Superseded", 3, [])); }
        public Task<ConnectionSnapshot> Snapshot(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) { Assert.Equal("https://managed.example", expectedServiceOrigin); return Task.FromResult(new ConnectionSnapshot(id, RevocationStatus, 3, [])); }
        public Task<ConnectionSnapshot> Disconnect(Guid id, CancellationToken ct, string? expectedServiceOrigin = null) => Snapshot(id, ct, expectedServiceOrigin);
    }
}
