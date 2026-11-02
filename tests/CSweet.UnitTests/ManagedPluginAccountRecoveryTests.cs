using System.Net;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ManagedPluginAccountRecoveryTests
{
    [Fact]
    public async Task ReadOnlyEligibilityThenFreshConsentRecoversSameGroupWithoutDeletingDataOrGrantingBusinessAccess()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, old) = await Failed(f);
        var originalGroup = old.AccountGroupId; var originalAuthorization = old.ConnectionId;
        var view = await f.Service.GetAsync(f.Admin, f.Id, default);
        var account = Assert.Single(view.Accounts); var offered = Assert.Single(account.Authorizations);
        Assert.True(offered.CanReconnect); Assert.True(offered.RequiresFreshConsentRecovery); Assert.False(account.ProviderVerified);
        Assert.Equal(1, gateway.Beginnings); Assert.Equal(0, gateway.Retirements); Assert.False(gateway.Cancelled);
        var next = await Fresh(f, old.ConnectionId);
        var imported = await f.Service.GetAsync(f.Admin, f.Id, default); account = Assert.Single(imported.Accounts);
        Assert.Equal(originalGroup, account.Id); Assert.True(account.ProviderVerified); Assert.Single(account.Resources); Assert.Empty(account.BusinessGrants);
        Assert.Empty(await f.Inner.Db.PluginAccountDeletions.ToArrayAsync());
        gateway.RetirementStatus = "Revoked";
        await f.Service.ProcessPendingAsync(default); await f.Service.ProcessPendingAsync(default);
        Assert.Equal(1, gateway.Retirements); Assert.False(gateway.Cancelled); Assert.Equal(0, f.Http.Exchanges);
        Assert.Null(await f.Secrets.GetAsync(f.Id, $"managed.context.{originalAuthorization:N}"));
        var current = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == next);
        Assert.Null(current.RetiredCredentialKey); Assert.Equal("Ready", current.Status);
        Assert.NotNull(await f.Secrets.GetAsync(f.Id, $"managed.context.{current.ConnectionId:N}"));
        Assert.Equal(PluginAccountState.Connected, (await f.Inner.Db.PluginAccountGroups.SingleAsync(x => x.Id == originalGroup)).State);
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(), x => x.Store == "external:managed-service-backups" && x.DeletedAt is null);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("AuthorizedPendingDiscovery")]
    [InlineData("RevocationPending")]
    [InlineData("ConnectionRequired")]
    public async Task NonterminalOrWrongRemoteStateDoesNotOfferOrBeginRecovery(string state)
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, old) = await Failed(f); gateway.RevocationStatus = state;
        var offered = Assert.Single(Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts).Authorizations);
        Assert.False(offered.CanReconnect); Assert.False(offered.RequiresFreshConsentRecovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest, old.ConnectionId), default, DeploymentProofCodec.Random()));
        Assert.Equal(1, gateway.Beginnings); Assert.Single(await f.Inner.Db.PluginConnections.Where(x => x.AccountGroupId != null).ToArrayAsync());
    }

    [Fact]
    public async Task RecoveryCannotExpandPermissionsAndUnavailableSnapshotCannotTriggerFallbackOrBegin()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, old) = await Failed(f);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.BeginAsync(f.Admin, f.Id, new(["base", "optional"], "v1", true, f.Digest, old.ConnectionId), default, DeploymentProofCodec.Random()));
        gateway.SnapshotFailure = new HttpRequestException("Synthetic safe failure");
        var offered = Assert.Single(Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts).Authorizations);
        Assert.False(offered.CanReconnect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest, old.ConnectionId), default, DeploymentProofCodec.Random()));
        Assert.Equal(1, gateway.Beginnings); Assert.False(gateway.Cancelled); Assert.Equal(0, f.Http.Exchanges);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("RevocationPending")]
    public async Task InvalidRetirementReceiptKeepsOldProofAndDoesNotFallBackToProviderRevocation(string response)
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, old) = await Failed(f);
        var fresh = await Fresh(f, old.ConnectionId); gateway.WrongRetirementId = response == "wrong-id";
        gateway.RetirementStatus = response == "wrong-id" ? "Revoked" : response;
        await f.Service.ProcessPendingAsync(default);
        var setup = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == fresh);
        Assert.NotNull(setup.RetiredCredentialKey); Assert.NotNull(await f.Secrets.GetAsync(f.Id, $"managed.context.{old.ConnectionId:N}"));
        Assert.False(gateway.Cancelled); Assert.Empty(await f.Inner.Db.PluginAccountDeletions.ToArrayAsync());
        Assert.Empty(await f.Inner.Db.PluginAccountDataCopies.Where(x => x.Store == "external:managed-service-backups").ToArrayAsync());
        gateway.WrongRetirementId = false; gateway.RetirementStatus = "Revoked";
        await f.Service.ProcessPendingAsync(default); Assert.Null(setup.RetiredCredentialKey); Assert.False(gateway.Cancelled);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("package")]
    [InlineData("profile")]
    [InlineData("partial-discovery")]
    [InlineData("verified-account")]
    [InlineData("not-expired")]
    public async Task ChangedOriginalAuthorityCannotOfferOrStartFreshConsent(string change)
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, old) = await Failed(f);
        switch (change)
        {
            case "actor": old.ApplicationUserId = f.Manager; break;
            case "package": old.PackageDigest = new string('c', 64); break;
            case "profile": old.ProfileHash = new string('c', 64); break;
            case "partial-discovery": old.PagesImported = 1; break;
            case "verified-account": (await f.Inner.Db.PluginAccountGroups.SingleAsync(x => x.Id == old.AccountGroupId)).IdentityKind = PluginAccountIdentityKind.ProviderSubject; break;
            case "not-expired": old.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10); break;
        }
        await f.Inner.Db.SaveChangesAsync();
        Assert.False(Assert.Single(Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts).Authorizations).CanReconnect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest, old.ConnectionId), default, DeploymentProofCodec.Random()));
        Assert.Equal(1, gateway.Beginnings); Assert.False(gateway.Cancelled);
        Assert.NotNull(await f.Secrets.GetAsync(f.Id, $"managed.context.{old.ConnectionId:N}"));
    }

    private static async Task<(ManagedPluginAccountFlowTests.Gateway Gateway, PluginAccountSetupSession Old)> Failed(PluginAccountFlowTests.Fixture f)
    {
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new ManagedPluginAccountFlowTests.Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!);
        f.Managed = gateway; var binding = DeploymentProofCodec.Random();
        var start = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, binding);
        await f.Service.CompleteManagedAsync(f.Admin, start.SetupId, new string('h', 43), binding, default);
        gateway.DiscoveryError = new HttpRequestException("Synthetic failure", null, HttpStatusCode.Forbidden);
        await f.Service.ImportAsync(start.SetupId, default); gateway.DiscoveryError = null;
        var old = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == start.SetupId);
        old.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-2); await f.Inner.Db.SaveChangesAsync();
        return (gateway, old);
    }
    private static async Task<Guid> Fresh(PluginAccountFlowTests.Fixture f, Guid previous)
    {
        var binding = DeploymentProofCodec.Random();
        var next = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest, previous), default, binding);
        await f.Service.CompleteManagedAsync(f.Admin, next.SetupId, new string('h', 43), binding, default);
        await f.Service.ImportAsync(next.SetupId, default); return next.SetupId;
    }
}
