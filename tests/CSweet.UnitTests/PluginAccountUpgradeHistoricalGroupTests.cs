using System.Text.Json;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class PluginAccountUpgradeTests
{
    private sealed record Historical(PluginAccountGroup Group,PluginConnection Connection,PluginAccountSetupSession Attempt,PluginAccountDataCopy Copy);

    private static async Task<Historical> AddHistorical(F f,string status="Expired",bool timestamp=false)
    {
        var group=new PluginAccountGroup{Id=Guid.NewGuid(),PluginInstallationId=f.Source.Id,ProviderProfile=f.Connection.ProviderProfile,
            DisplayName="Historical synthetic attempt",State=PluginAccountState.ConnectionRequired,IdentityKind=PluginAccountIdentityKind.Administrative};
        var connection=new PluginConnection{Id=Guid.NewGuid(),AgentInstallationId=f.Source.Id,AccountGroupId=group.Id,
            ProviderProfile=group.ProviderProfile,DeclarationId=f.Connection.DeclarationId,Status=PluginConnectionStatus.Revoked,
            RevokedAt=timestamp?DateTimeOffset.UtcNow.AddDays(-1):null};
        var attempt=new PluginAccountSetupSession{Id=Guid.NewGuid(),PluginInstallationId=f.Source.Id,AccountGroupId=group.Id,ConnectionId=connection.Id,
            ApplicationUserId=f.User,Status=status,ExpiresAt=DateTimeOffset.UtcNow.AddDays(-1)};
        var copy=new PluginAccountDataCopy{Id=Guid.NewGuid(),AccountGroupId=group.Id,ConsumerInstallationId=f.Source.Id,
            Store="external:synthetic-backup",RecordId=attempt.Id};
        f.Db.AddRange(group,connection,attempt,copy);await f.Db.SaveChangesAsync();return new(group,connection,attempt,copy);
    }

    private static async Task<string> RetainedHistory(F f,Historical h)=>JsonSerializer.Serialize(new{
        Group=await f.Db.PluginAccountGroups.AsNoTracking().SingleAsync(x=>x.Id==h.Group.Id),
        Connection=await f.Db.PluginConnections.AsNoTracking().SingleAsync(x=>x.Id==h.Connection.Id),
        Attempt=await f.Db.PluginAccountSetupSessions.AsNoTracking().SingleAsync(x=>x.Id==h.Attempt.Id),
        Copy=await f.Db.PluginAccountDataCopies.AsNoTracking().SingleAsync(x=>x.Id==h.Copy.Id)});

    [Theory]
    [InlineData("Expired",false)][InlineData("Cancelled",true)][InlineData("ImportFailed",false)]
    public async Task EmptyExpiredHistoricalAccountDoesNotTransferOrEraseHistoryOrRestoreAuthority(string terminal,bool timestamp)
    {
        await using var f=await F.Create();var old=await AddHistorical(f,terminal,timestamp);var before=await RetainedHistory(f,old);
        var secret=await f.Db.PluginSecrets.AsNoTracking().SingleAsync();var grant=await f.Db.PluginAccountBusinessGrants.AsNoTracking().SingleAsync();
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        Assert.Equal(1,review.ResourceCount);Assert.Equal(1,review.BusinessGrantCount);Assert.Equal(new[]{"base"},review.GrantedScopeSets);
        Assert.Equal(before,await RetainedHistory(f,old));Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());Assert.True(f.Source.IsEnabled);
        var result=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);Assert.Equal("Completed",result.Status);
        Assert.Equal(before,await RetainedHistory(f,old));Assert.Equal(f.Target.Id,(await f.Db.PluginAccountGroups.AsNoTracking().SingleAsync(x=>x.Id==f.Account.Id)).PluginInstallationId);
        Assert.Equal(f.Target.Id,(await f.Db.PluginConnections.AsNoTracking().SingleAsync(x=>x.Id==f.Connection.Id)).AgentInstallationId);
        var currentSecret=await f.Db.PluginSecrets.AsNoTracking().SingleAsync();Assert.Equal(secret.Id,currentSecret.Id);Assert.Equal(secret.ProtectedValue,currentSecret.ProtectedValue);
        var currentGrant=await f.Db.PluginAccountBusinessGrants.AsNoTracking().SingleAsync();Assert.Equal(grant.Id,currentGrant.Id);
        Assert.Equal(grant.CapabilitiesJson,currentGrant.CapabilitiesJson);Assert.Equal(grant.SelectedResourceIdsJson,currentGrant.SelectedResourceIdsJson);
        Assert.Null((await f.Db.PluginAccountDataCopies.AsNoTracking().SingleAsync(x=>x.Id==old.Copy.Id)).DeletedAt);
        Assert.Empty(await f.Db.PluginAccountDeletions.ToArrayAsync());Assert.Empty(await f.Db.PluginAccountCleanupReceipts.ToArrayAsync());
        Assert.Equal(1,f.Gateway.Adopts);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);Assert.Equal(0,f.Gateway.Discoveries);
    }

    [Theory]
    [InlineData("verified-kind")][InlineData("group-subject")][InlineData("group-authority")][InlineData("group-state")]
    [InlineData("connection-subject")][InlineData("connection-active")][InlineData("connection-pending")][InlineData("connection-lease")]
    [InlineData("connection-lease-until")][InlineData("credential-attempt")][InlineData("cross-installation")][InlineData("profile")]
    [InlineData("attempt-live")][InlineData("attempt-ready")][InlineData("attempt-unexpired")][InlineData("attempt-subject")]
    [InlineData("attempt-authority")][InlineData("attempt-pages")][InlineData("attempt-observed")][InlineData("attempt-retirement")]
    [InlineData("attempt-lease")][InlineData("attempt-lease-until")][InlineData("attempt-maintenance")][InlineData("attempt-wrong-connection")]
    [InlineData("resource")][InlineData("historical-grant")][InlineData("retained-secret")][InlineData("missing-attempt")][InlineData("missing-connection")]
    [InlineData("bound-resource")][InlineData("resource-proof")][InlineData("historical-resource-proof")]
    public async Task HistoricalLookalikesWithIdentityAuthorityOrUnresolvedWorkStillFailClosed(string change)
    {
        await using var f=await F.Create();var h=await AddHistorical(f);
        switch(change)
        {
            case "verified-kind":h.Group.IdentityKind=PluginAccountIdentityKind.ProviderSubject;break;
            case "group-subject":h.Group.VerifiedSubject="synthetic-subject";break;
            case "group-authority":h.Group.IdentityAuthority="https://api.example.com/identity";break;
            case "group-state":h.Group.State=PluginAccountState.Disconnecting;break;
            case "connection-subject":h.Connection.ExternalAccountId="synthetic-subject";break;
            case "connection-active":h.Connection.Status=PluginConnectionStatus.Connected;break;
            case "connection-pending":h.Connection.Status=PluginConnectionStatus.Pending;break;
            case "connection-lease":h.Connection.RefreshLeaseOwner=Guid.NewGuid();break;
            case "connection-lease-until":h.Connection.RefreshLeaseUntil=DateTimeOffset.UtcNow.AddMinutes(-1);break;
            case "credential-attempt":h.Connection.ActiveCredentialAttemptId=h.Attempt.Id;break;
            case "bound-resource":h.Connection.BoundResourceId="synthetic-resource";break;
            case "cross-installation":h.Connection.AgentInstallationId=f.Target.Id;break;
            case "profile":h.Group.ProviderProfile="another.synthetic.profile";break;
            case "attempt-live":h.Attempt.Status="AwaitingConsent";break;
            case "attempt-ready":h.Attempt.Status="Ready";break;
            case "attempt-unexpired":h.Attempt.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(1);break;
            case "attempt-subject":h.Attempt.VerifiedSubject="synthetic-subject";break;
            case "attempt-authority":h.Attempt.IdentityAuthority="https://api.example.com/identity";break;
            case "attempt-pages":h.Attempt.PagesImported=1;break;
            case "attempt-observed":h.Attempt.ObservedResourcesJson="[{}]";break;
            case "attempt-retirement":h.Attempt.RetiredCredentialKey="synthetic-reference";break;
            case "attempt-lease":h.Attempt.LeaseOwner=Guid.NewGuid();break;
            case "attempt-lease-until":h.Attempt.LeaseUntil=DateTimeOffset.UtcNow.AddMinutes(-1);break;
            case "attempt-maintenance":h.Attempt.IsMaintenance=true;break;
            case "attempt-wrong-connection":h.Attempt.ConnectionId=f.Connection.Id;break;
            case "resource":f.Db.PluginAccountResources.Add(new(){Id=Guid.NewGuid(),AccountGroupId=h.Group.Id,PluginInstallationId=f.Source.Id,IsAvailable=false});break;
            case "resource-proof":case "historical-resource-proof":
                f.Db.PluginAccountResourceAuthorizations.Add(new(){ResourceId=(await f.Db.PluginAccountResources.SingleAsync()).Id,
                    ConnectionId=h.Connection.Id,IsAvailable=change=="resource-proof"});break;
            case "historical-grant":f.Db.PluginAccountBusinessGrants.Add(new(){Id=Guid.NewGuid(),AccountGroupId=h.Group.Id,PluginInstallationId=f.Source.Id,RevokedAt=DateTimeOffset.UtcNow});break;
            case "retained-secret":await f.Store.SetAsync(f.Source.Id,"managed.context."+h.Connection.Id.ToString("N"),"synthetic-private-reference");break;
            case "missing-attempt":f.Db.PluginAccountSetupSessions.Remove(h.Attempt);break;
            case "missing-connection":f.Db.PluginConnections.Remove(h.Connection);break;
        }
        await f.Db.SaveChangesAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ReviewAsync(f.User,f.Target.Id,default));
        Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());Assert.Equal(0,f.Gateway.Adopts);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
        Assert.True((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
    }

    [Theory]
    [InlineData("group")][InlineData("connection")][InlineData("attempt")]
    public async Task HistoricalSnapshotChangeInvalidatesReviewBeforeIntentOrRemoteAdoption(string changed)
    {
        await using var f=await F.Create();var h=await AddHistorical(f);var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        if(changed=="group")h.Group.Revision++;if(changed=="connection")h.Connection.AuthorizationRevision++;if(changed=="attempt")h.Attempt.Revision++;
        await f.Db.SaveChangesAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default));
        Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());Assert.Equal(0,f.Gateway.Adopts);Assert.True(f.Source.IsEnabled);
    }

    [Fact]
    public async Task ChangedHistoricalStateAfterLostAcknowledgmentKeepsBothRevisionsFenced()
    {
        await using var f=await F.Create();var h=await AddHistorical(f);var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        f.Gateway.LoseAcknowledgment=true;var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);
        Assert.Equal("Pending",pending.Status);
        // Approval clears the identity map while reconciling the remote receipt. Change
        // the current persisted row, not the fixture's now-detached original entity.
        var attempt=await f.Db.PluginAccountSetupSessions.SingleAsync(x=>x.Id==h.Attempt.Id);
        attempt.LeaseUntil=DateTimeOffset.UtcNow.AddMinutes(1);await f.Db.SaveChangesAsync();
        Assert.NotNull((await f.Db.PluginAccountSetupSessions.AsNoTracking().SingleAsync(x=>x.Id==h.Attempt.Id)).LeaseUntil);
        Assert.Equal("Pending",(await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default)).Status);Assert.Equal(1,f.Gateway.Adopts);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
        Assert.Equal(f.Source.Id,(await f.Db.PluginSecrets.AsNoTracking().SingleAsync()).PluginInstallationId);
    }

    [Fact]
    public async Task RelationalRollbackAndReceiptRecoveryPreserveHistoricalRowsAndOutstandingCopy()
    {
        var failure=new FinalizeFailure();await using var f=await F.Relational(failure);var h=await AddHistorical(f);var before=await RetainedHistory(f,h);
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);failure.Armed=true;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);Assert.Equal("Pending",pending.Status);
        Assert.Equal(before,await RetainedHistory(f,h));Assert.Equal(f.Source.Id,(await f.Db.PluginSecrets.AsNoTracking().SingleAsync()).PluginInstallationId);
        Assert.Equal("Completed",(await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default)).Status);
        Assert.Equal(before,await RetainedHistory(f,h));Assert.Equal(1,f.Gateway.Adopts);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
    }

    [AdoptionPostgresFact]
    public async Task PostgresCompletionRetainsHistoricalRowsAndOutstandingCopyThroughRealMigrationGuards()
    {
        await using var f=await F.Postgres(Environment.GetEnvironmentVariable("CSWEET_ADOPTION_POSTGRES_TEST_FILE")!);
        var h=await AddHistorical(f);var before=await RetainedHistory(f,h);
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        Assert.Equal("Completed",(await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default)).Status);
        Assert.Equal(before,await RetainedHistory(f,h));Assert.Equal(1,f.Gateway.Adopts);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
        Assert.Equal(f.Target.Id,(await f.Db.PluginSecrets.AsNoTracking().SingleAsync()).PluginInstallationId);
    }

    [AdoptionPostgresFact]
    public async Task PostgresHistoricalChangeFromAnotherContextRejectsStaleReviewBeforeRemoteAdoption()
    {
        await using var f=await F.Postgres(Environment.GetEnvironmentVariable("CSWEET_ADOPTION_POSTGRES_TEST_FILE")!);
        var h=await AddHistorical(f);var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        await using(var other=new CSweet.Infrastructure.Persistence.CSweetDbContext(new DbContextOptionsBuilder<CSweet.Infrastructure.Persistence.CSweetDbContext>()
            .UseNpgsql(f.Db.Database.GetConnectionString()).Options))
        {
            var attempt=await other.PluginAccountSetupSessions.SingleAsync(x=>x.Id==h.Attempt.Id);attempt.Revision++;await other.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default));
        Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());Assert.Equal(0,f.Gateway.Adopts);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
        Assert.True((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
    }
}
