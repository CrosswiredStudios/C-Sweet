using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class PluginAccountUpgradeTests
{
    [Fact]
    public async Task ReviewedUpgradeKeepsExactlyTheExistingIdentityResourcesScopesAndSelectedBusinessGrant()
    {
        await using var f=await F.Create();var secret=await f.Db.PluginSecrets.AsNoTracking().SingleAsync();
        var grant=await f.Db.PluginAccountBusinessGrants.AsNoTracking().SingleAsync();var original=await f.Db.PluginAccountSetupSessions.AsNoTracking().SingleAsync();
        var resource=await f.Db.PluginAccountResources.AsNoTracking().SingleAsync();
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        Assert.True(f.Source.IsEnabled);Assert.Equal(0,f.Gateway.Adopts);Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());
        var result=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);
        Assert.Equal("Completed",result.Status);Assert.Equal(1,f.Gateway.Adopts);
        var current=await f.Db.PluginConnections.AsNoTracking().SingleAsync();Assert.Equal(f.Target.Id,current.AgentInstallationId);
        Assert.Equal(f.Account.Id,current.AccountGroupId);Assert.Equal(f.Account.VerifiedSubject,current.ExternalAccountId);
        Assert.Equal("[\"read\",\"identity\"]",current.GrantedScopesJson);
        Assert.Equal(f.Target.Id,(await f.Db.PluginAccountGroups.AsNoTracking().SingleAsync()).PluginInstallationId);
        var moved=await f.Db.PluginAccountBusinessGrants.AsNoTracking().SingleAsync();
        Assert.Equal(grant.Id,moved.Id);Assert.Equal(grant.OrganizationId,moved.OrganizationId);Assert.Equal(grant.CapabilitiesJson,moved.CapabilitiesJson);
        Assert.Equal(grant.SelectedResourceIdsJson,moved.SelectedResourceIdsJson);Assert.Equal(grant.Revision+1,moved.Revision);
        Assert.Equal(f.Target.PackageVersion!.PackageDigest,moved.PackageDigest);Assert.Equal(f.Target.Id,moved.PluginInstallationId);
        var movedResource=await f.Db.PluginAccountResources.AsNoTracking().SingleAsync();Assert.Equal(resource.Id,movedResource.Id);Assert.Equal(resource.ExternalResourceId,movedResource.ExternalResourceId);
        var movedSecret=await f.Db.PluginSecrets.AsNoTracking().SingleAsync();Assert.Equal(secret.Id,movedSecret.Id);Assert.Equal(secret.ProtectedValue,movedSecret.ProtectedValue);Assert.Equal(f.Target.Id,movedSecret.PluginInstallationId);
        var history=await f.Db.PluginAccountSetupSessions.AsNoTracking().SingleAsync();Assert.Equal(original.PluginInstallationId,history.PluginInstallationId);Assert.Equal(original.PackageDigest,history.PackageDigest);
        Assert.True((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
        Assert.Equal(PluginRevisionStatus.Retired,(await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).RevisionStatus);
        Assert.Empty(await f.Db.AgentCapabilityBindings.ToArrayAsync());Assert.Empty(await f.Db.PluginAccountDeletions.ToArrayAsync());Assert.Empty(await f.Db.PluginAccountCleanupReceipts.ToArrayAsync());
        Assert.Null((await f.Db.PluginAccountDataCopies.SingleAsync()).DeletedAt);
        Assert.Equal(result,await f.Service.ResumeAsync(f.User,f.Target.Id,result.Id,default));Assert.Equal(1,f.Gateway.Adopts);
    }

    [Fact]
    public async Task LostServiceAcknowledgmentIsPendingThenRecoveredFromTheSameReceiptWithoutAnotherPost()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);f.Gateway.LoseAcknowledgment=true;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);
        Assert.Equal("Pending",pending.Status);Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
        Assert.Equal(f.Source.Id,(await f.Db.PluginSecrets.SingleAsync()).PluginInstallationId);
        Assert.Equal("Completed",(await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default)).Status);Assert.Equal(1,f.Gateway.Adopts);
    }

    [Theory]
    [InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task ImpactReviewedResumeCannotClearPendingSourceOrTargetFenceAndTerminalRecoveryLeavesNoStaleControlFence(bool accepted,bool target)
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        f.Gateway.FailBeforeAdopt=!accepted;f.Gateway.LoseAcknowledgment=accepted;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);Assert.Equal("Pending",pending.Status);
        var accounts=new PluginAccountService(f.Db,f.Store,f.Protection,f.Inner.Http,f.Inner.Profiles,f.Inner.Transport,
            Options.Create(f.Inner.ConnectionOptions),new TestAuditEventWriter(),managed:f.Gateway);
        var id=target?f.Target.Id:f.Source.Id;var impact=await accounts.GetInstallationImpactAsync(f.User,id,default);
        var administration=await f.Db.PluginAccountAdministrations.AsNoTracking().ToArrayAsync();
        var revision=administration.SingleOrDefault(x=>x.PluginInstallationId==id)?.Revision??1;
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>accounts.ControlInstallationAsync(f.User,id,new("Resume",impact.ReviewHash,revision),default));
        Assert.Contains("compatible account upgrade is pending",error.Message);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
        Assert.Equal(administration.Length,await f.Db.PluginAccountAdministrations.CountAsync());
        Assert.Equal("Pending",(await f.Db.PluginAccountUpgrades.AsNoTracking().SingleAsync()).Status);Assert.Equal(1,f.Gateway.Adopts);
        Assert.Equal(f.Source.Id,(await f.Db.PluginSecrets.SingleAsync()).PluginInstallationId);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
        var terminal=accepted?await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default):await f.Service.CancelAsync(f.User,f.Target.Id,pending.Id,default);
        Assert.Equal(accepted?"Completed":"Cancelled",terminal.Status);Assert.Equal(1,f.Gateway.Adopts);
        // The old impact is invalid after the intent becomes terminal, even if a status flag happens to match.
        await Assert.ThrowsAsync<InvalidOperationException>(()=>accounts.ControlInstallationAsync(f.User,id,new("Resume",impact.ReviewHash,revision),default));
        var active=accepted?f.Target.Id:f.Source.Id;var current=await accounts.GetInstallationImpactAsync(f.User,active,default);
        var currentRevision=(await f.Db.PluginAccountAdministrations.AsNoTracking().SingleOrDefaultAsync(x=>x.PluginInstallationId==active))?.Revision??1;
        await accounts.ControlInstallationAsync(f.User,active,new("Resume",current.ReviewHash,currentRevision),default);
        Assert.True((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==active)).IsEnabled);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
    }

    [Fact]
    public async Task UnsentIntentCanBeCancelledWithServiceTombstoneAndOnlyUnchangedSourceResumed()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);f.Gateway.FailBeforeAdopt=true;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);
        Assert.Equal("Pending",pending.Status);Assert.Equal(1,f.Gateway.Adopts);
        var cancelled=await f.Service.CancelAsync(f.User,f.Target.Id,pending.Id,default);Assert.Equal("Cancelled",cancelled.Status);
        Assert.True((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
        Assert.Equal(f.Source.Id,(await f.Db.PluginConnections.SingleAsync()).AgentInstallationId);Assert.Equal(1,f.Gateway.Cancels);
    }

    [Fact]
    public async Task CancellationAfterServiceAcceptedAdoptionCompletesForwardRatherThanRestoringOldAuthority()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);f.Gateway.LoseAcknowledgment=true;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);
        Assert.Equal("Completed",(await f.Service.CancelAsync(f.User,f.Target.Id,pending.Id,default)).Status);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);Assert.Equal(1,f.Gateway.Adopts);
    }

    [Theory]
    [InlineData("grant")][InlineData("resource")][InlineData("origin")][InlineData("profile")][InlineData("disconnect")]
    public async Task ChangedAuthorityWhilePendingCannotActivateTargetOrRestoreOldGrants(string change)
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);f.Gateway.LoseAcknowledgment=true;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);
        if(change=="grant")(await f.Db.PluginAccountBusinessGrants.SingleAsync()).RevokedAt=DateTimeOffset.UtcNow;
        if(change=="resource")(await f.Db.PluginAccountResources.SingleAsync()).IsAvailable=false;
        if(change=="origin")f.Gateway.Origin="https://another.example";
        if(change=="profile")f.Gateway.ProfileBinding=new string('d',64);
        if(change=="disconnect")(await f.Db.PluginAccountGroups.SingleAsync()).State=PluginAccountState.Disconnecting;
        await f.Db.SaveChangesAsync();var result=await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default);
        Assert.Equal("Pending",result.Status);Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
        Assert.Equal(f.Source.Id,(await f.Db.PluginAccountGroups.SingleAsync()).PluginInstallationId);Assert.Equal(1,f.Gateway.Adopts);
    }

    [Theory]
    [InlineData("binding")][InlineData("operator")][InlineData("pending-consent")][InlineData("lease")][InlineData("whole-account")]
    [InlineData("grant-package")][InlineData("secret")][InlineData("other-lineage")][InlineData("configuration")][InlineData("source-schedule")][InlineData("target-schedule")]
    public async Task UnsupportedOrAmbiguousStateFailsBeforeCreatingIntentOrCallingAdopt(string change)
    {
        await using var f=await F.Create();
        if(change is "source-schedule" or "target-schedule")f.Db.AgentSchedules.Add(new(){Id=Guid.NewGuid(),AgentInstallationId=change=="source-schedule"?f.Source.Id:f.Target.Id,IsEnabled=true});
        if(change=="binding")f.Db.AgentCapabilityBindings.Add(new(){Id=Guid.NewGuid(),ProviderInstallationId=f.Source.Id,RequesterInstallationId=f.Inner.Inner.Requester.Id});
        if(change=="operator")f.Db.PluginAccountOperators.Add(new(){Id=Guid.NewGuid(),PluginInstallationId=f.Source.Id,ApplicationUserId=f.Inner.Manager});
        if(change=="pending-consent")(await f.Db.PluginAccountSetupSessions.SingleAsync()).Status="AwaitingConsent";
        if(change=="lease")f.Connection.RefreshLeaseOwner=Guid.NewGuid();
        if(change=="whole-account")(await f.Db.PluginAccountBusinessGrants.SingleAsync()).Scope=PluginAccountGrantScope.WholeAccount;
        if(change=="grant-package")(await f.Db.PluginAccountBusinessGrants.SingleAsync()).PackageDigest=new string('e',64);
        if(change=="secret")await f.Store.SetAsync(f.Source.Id,"unknown.reference","synthetic-not-provider-data");
        if(change=="other-lineage")f.Target.InstallationKey=Guid.NewGuid();
        if(change=="configuration")f.Db.AgentInstallationConfigurations.Add(new(){Id=Guid.NewGuid(),AgentInstallationId=f.Target.Id,SchemaVersion="1",SettingsJson="{\"setting\":\"different\"}"});
        await f.Db.SaveChangesAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ReviewAsync(f.User,f.Target.Id,default));
        Assert.Equal(0,f.Gateway.Adopts);Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());Assert.True(f.Source.IsEnabled);
    }

    [Fact]
    public async Task ReviewTokenIsBoundToAdministratorTargetAndCurrentGrantSnapshot()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ApproveAsync(f.Inner.Manager,f.Target.Id,new(review.ReviewToken),default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApproveAsync(f.User,f.Target.Id,new("malformed"),default));
        (await f.Db.PluginAccountBusinessGrants.SingleAsync()).Revision++;await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default));
        Assert.Empty(await f.Db.PluginAccountUpgrades.ToArrayAsync());Assert.Equal(0,f.Gateway.Adopts);
    }

    [Fact]
    public async Task CompletedAdoptionRetainsOriginalConsentProvenanceForRealDisconnectWorkerAndPendingBackupObligations()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        var completed=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);Assert.Equal("Completed",completed.Status);
        var accounts=new PluginAccountService(f.Db,f.Store,f.Protection,f.Inner.Http,f.Inner.Profiles,f.Inner.Transport,
            Options.Create(f.Inner.ConnectionOptions),new TestAuditEventWriter(),managed:f.Gateway);
        var impact=await accounts.GetDisconnectImpactAsync(f.User,f.Target.Id,f.Account.Id,default);
        await accounts.DisconnectAsync(f.User,f.Target.Id,f.Account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        await accounts.ProcessPendingAsync(default);
        Assert.Equal(f.Original.Id,f.Gateway.CancelledAttempt);Assert.Equal(1,f.Gateway.ProviderRevocationCalls);
        Assert.Empty(await f.Db.PluginSecrets.ToArrayAsync());Assert.Null((await f.Db.PluginAccountDeletions.SingleAsync()).CompletedAt);
        Assert.Contains(await f.Db.PluginAccountDataCopies.ToArrayAsync(),x=>x.Store=="external:managed-service-backups" && x.DeletedAt==null);
        Assert.Equal(PluginAccountState.Disconnecting,(await f.Db.PluginAccountGroups.SingleAsync()).State);
    }

    [Fact]
    public async Task TerminalJournalAndItsAuthorityCannotBeRewrittenOrDeleted()
    {
        await using var f=await F.Create();var r=await f.Service.ReviewAsync(f.User,f.Target.Id,default);var done=await f.Service.ApproveAsync(f.User,f.Target.Id,new(r.ReviewToken),default);
        var row=await f.Db.PluginAccountUpgrades.SingleAsync();row.ServiceOrigin="https://another.example";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task MetadataMaintenanceUsesTheAdoptedPackageWithoutNewConsentAndRetainsResourceIdentity()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        Assert.Equal("Completed",(await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default)).Status);
        f.Gateway.AllowDiscovery=true;var connection=await f.Db.PluginConnections.SingleAsync();connection.NextInventoryCheckAt=DateTimeOffset.UtcNow.AddMinutes(-1);await f.Db.SaveChangesAsync();
        var before=await f.Db.PluginAccountResources.AsNoTracking().SingleAsync();
        var accounts=new PluginAccountService(f.Db,f.Store,f.Protection,f.Inner.Http,f.Inner.Profiles,f.Inner.Transport,
            Options.Create(f.Inner.ConnectionOptions),new TestAuditEventWriter(),managed:f.Gateway);
        await accounts.ProcessPendingAsync(default);
        Assert.Equal(1,f.Gateway.Discoveries);Assert.Equal(0,f.Inner.Http.Exchanges);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
        var after=await f.Db.PluginAccountResources.AsNoTracking().SingleAsync();Assert.Equal(before.Id,after.Id);Assert.Equal(before.ExternalResourceId,after.ExternalResourceId);
        Assert.Equal("Ready",(await f.Db.PluginAccountSetupSessions.SingleAsync(x=>x.IsMaintenance)).Status);
    }

    [Fact]
    public async Task ASecondCompatibleUpgradeKeepsTheOriginalConsentJournalAndProtectedReference()
    {
        await using var f=await F.Create();var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        Assert.Equal("Completed",(await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default)).Status);
        var prior=await f.Db.AgentInstallations.Include(x=>x.PackageVersion).SingleAsync(x=>x.Id==f.Target.Id);
        var nextJson=JsonNode.Parse(prior.PackageVersion!.ManifestJson)!;nextJson["version"]="0.1.2";
        var next=new AgentInstallation{Id=Guid.NewGuid(),InstallationKey=prior.InstallationKey,RevisionNumber=3,RevisionStatus=PluginRevisionStatus.Staged,
            SupersedesInstallationId=prior.Id,Scope=PluginInstallationScope.System,BusinessId="enterprise",IsEnabled=false,Grant=new(){Id=Guid.NewGuid()},
            PackageVersion=new(){Id=Guid.NewGuid(),PackageSourceId=prior.PackageVersion.PackageSourceId,AgentId=prior.PackageVersion.AgentId,PublisherId=prior.PackageVersion.PublisherId,
                Version="0.1.2",PluginKind=PluginKind.Connector,Status=AgentPackageVersionStatus.Built,PackageDigest=new string('e',64),ManifestJson=nextJson.ToJsonString()}};
        f.Db.AgentInstallations.Add(next);await f.Db.SaveChangesAsync();
        f.Gateway.Saved=null;f.Gateway.SourceRevision=4;
        var second=await f.Service.ReviewAsync(f.User,next.Id,default);Assert.Equal("Completed",(await f.Service.ApproveAsync(f.User,next.Id,new(second.ReviewToken),default)).Status);
        Assert.Equal(2,f.Gateway.Adopts);Assert.Equal(f.Original.Id,(await ManagedAccountProvenance.Original(f.Db,next.Id,f.Connection.Id,default)).Id);
        Assert.Equal(f.Source.Id,(await f.Db.PluginAccountSetupSessions.SingleAsync()).PluginInstallationId);
        Assert.Equal(next.Id,(await f.Db.PluginSecrets.SingleAsync()).PluginInstallationId);Assert.Equal(2,await f.Db.PluginAccountUpgrades.CountAsync());
    }

    [Fact]
    public async Task RelationalCommitFailureRollsBackNativeOwnershipAndEncryptedReferenceThenRecoversTheExistingServiceReceipt()
    {
        var failure=new FinalizeFailure();await using var f=await F.Relational(failure);
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);failure.Armed=true;
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);Assert.Equal("Pending",pending.Status);
        Assert.Equal(f.Source.Id,(await f.Db.PluginAccountGroups.SingleAsync()).PluginInstallationId);
        Assert.Equal(f.Source.Id,(await f.Db.PluginSecrets.SingleAsync()).PluginInstallationId);
        Assert.Equal(f.Inner.Digest,(await f.Db.PluginAccountBusinessGrants.SingleAsync()).PackageDigest);
        Assert.Equal("Pending",(await f.Db.PluginAccountUpgrades.SingleAsync()).Status);
        Assert.Equal("Completed",(await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default)).Status);Assert.Equal(1,f.Gateway.Adopts);
    }

    // Test-only storage adapter: production uses PostgreSQL timestamps/identity sequences. Keep
    // actual relational transactions, foreign keys and concurrency while giving SQLite sortable UTC timestamps.
    public sealed class SqliteModelCustomizer(Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizerDependencies dependencies)
        :Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder model,DbContext context)
        {
            base.Customize(model,context);
            foreach(var property in model.Model.GetEntityTypes().SelectMany(x=>x.GetProperties()))
                if(property.ClrType==typeof(DateTimeOffset) || property.ClrType==typeof(DateTimeOffset?))
                {property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());property.SetColumnType("INTEGER");}
        }
    }

    private sealed class SqliteUtcClock:IQueryExpressionInterceptor
    {
        public System.Linq.Expressions.Expression QueryCompilationStarting(System.Linq.Expressions.Expression query,QueryExpressionEventData data)
            =>new ClockExpression().Visit(query);
        private sealed class ClockExpression:System.Linq.Expressions.ExpressionVisitor
        {
            protected override System.Linq.Expressions.Expression VisitMember(System.Linq.Expressions.MemberExpression node)
                =>node.Member.DeclaringType==typeof(DateTimeOffset) && node.Member.Name==nameof(DateTimeOffset.UtcNow)
                    ?System.Linq.Expressions.Expression.Constant(DateTimeOffset.UtcNow):base.VisitMember(node);
        }
    }

    private sealed class SqliteOutboxSequence:SaveChangesInterceptor
    {
        private long next=100000;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            foreach(var entry in data.Context!.ChangeTracker.Entries<CSweet.Domain.Notifications.ApplicationRealtimeOutboxItem>().Where(x=>x.State==EntityState.Added))
                entry.Entity.Sequence=++next;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FinalizeFailure:SaveChangesInterceptor
    {
        public bool Armed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(Armed && data.Context!.ChangeTracker.Entries<PluginAccountUpgrade>().Any(x=>x.State==EntityState.Modified && x.Entity.Status=="Completed"))
            {Armed=false;Assert.NotNull(data.Context.Database.CurrentTransaction);throw new DbUpdateException("synthetic local commit failure");}
            return ValueTask.FromResult(result);
        }
    }

    internal sealed class F:IAsyncDisposable
    {
        public PluginAccountFlowTests.Fixture Inner=null!;private CSweetDbContext? relational;private System.Data.Common.DbConnection? connection;
        public CSweetDbContext Db=>relational??Inner.Inner.Db;public Guid User=>Inner.Admin;
        public AgentInstallation Source=>Inner.Inner.Connector;public AgentInstallation Target=null!;public PluginAccountGroup Account=null!;
        public PluginConnection Connection=>Inner.Inner.Connection;public PluginAccountSetupSession Original=null!;
        public readonly IDataProtectionProvider Protection=new EphemeralDataProtectionProvider();public DataProtectionPluginSecretStore Store=null!;
        public Gateway Gateway=null!;public PluginAccountUpgradeService Service=>new(Db,Store,Store,Gateway,Protection,new TestAuditEventWriter());
        public async ValueTask DisposeAsync(){if(relational is not null)await relational.DisposeAsync();if(connection is not null)await connection.DisposeAsync();await Inner.DisposeAsync();}
        public static async Task<F> Relational(IInterceptor interceptor)
        {
            var f=await Create();var source=new AgentPackageSource{Id=Guid.NewGuid(),RepositoryUrl="https://github.com/example/synthetic",RepositoryOwner="example",RepositoryName="synthetic"};
            f.Db.AgentPackageSources.Add(source);foreach(var p in await f.Db.AgentPackageVersions.ToArrayAsync())
            {p.PackageSourceId=source.Id;p.CommitSha=p.Id.ToString("N").PadRight(40,'0');p.ManifestDigest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.ManifestJson)));}await f.Db.SaveChangesAsync();
            var rows=f.Db.ChangeTracker.Entries().Where(x=>x.State==EntityState.Unchanged).Select(x=>x.Entity).ToArray();f.Db.ChangeTracker.Clear();
            f.connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");await f.connection.OpenAsync();
            var sql=new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseSqlite(f.connection).ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer,SqliteModelCustomizer>().AddInterceptors(new SqliteUtcClock(),new SqliteOutboxSequence(),interceptor).Options);
            await sql.Database.EnsureCreatedAsync();sql.AddRange(rows);await sql.SaveChangesAsync();f.relational=sql;f.Store=new(sql,f.Protection);return f;
        }
        public static async Task<F> Postgres(string settingsFile,IInterceptor? interceptor=null)
        {
            var settings=new Npgsql.NpgsqlConnectionStringBuilder(await File.ReadAllTextAsync(settingsFile));
            if(settings.Host!="127.0.0.1" || settings.Port!=5432 || settings.Database!="csweet_adoption_test" || settings.Username!="postgres" || settings.Pooling)
                throw new InvalidOperationException("Only the disposable, network-isolated adoption fixture is supported.");
            settings.Database="csweet_adoption_test_"+Guid.NewGuid().ToString("N");
            var f=await Create();var source=new AgentPackageSource{Id=Guid.NewGuid(),RepositoryUrl="https://github.com/example/synthetic",RepositoryOwner="example",RepositoryName="synthetic"};
            f.Db.AgentPackageSources.Add(source);foreach(var package in await f.Db.AgentPackageVersions.ToArrayAsync())
            {package.PackageSourceId=source.Id;package.CommitSha=package.Id.ToString("N").PadRight(40,'0');package.ManifestDigest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(package.ManifestJson)));}
            await f.Db.SaveChangesAsync();var rows=f.Db.ChangeTracker.Entries().Where(x=>x.State==EntityState.Unchanged).Select(x=>x.Entity).ToArray();f.Db.ChangeTracker.Clear();
            f.connection=new Npgsql.NpgsqlConnection(settings.ConnectionString);
            var builder=new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(f.connection);if(interceptor is not null)builder.AddInterceptors(interceptor);
            var sql=new CSweetDbContext(builder.Options);await sql.Database.EnsureCreatedAsync();await sql.Database.OpenConnectionAsync();
            // Install the real additive migration, not a hand-written trigger imitation, into the fresh model fixture.
            await sql.Database.ExecuteSqlRawAsync("DROP TABLE \"PluginAccountUpgrades\"");
            var history=Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>(sql);
            await sql.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
            var migrations=sql.Database.GetMigrations().ToArray();var index=Array.FindIndex(migrations,x=>x.EndsWith("_AddCompatiblePluginAccountUpgrades",StringComparison.Ordinal));
            var migrator=Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(sql);
            await sql.Database.ExecuteSqlRawAsync(migrator.GenerateScript(migrations[index-1],migrations[index]));
            sql.AddRange(rows);await sql.SaveChangesAsync();f.relational=sql;f.Store=new(sql,f.Protection);return f;
        }
        public static async Task<F> Create()
        {
            var f=new F{Inner=await PluginAccountFlowTests.Fixture.Create()};var db=f.Db;
            db.AgentCapabilityBindings.RemoveRange(await db.AgentCapabilityBindings.ToArrayAsync());
            f.Source.IsEnabled=true;f.Source.InstallationKey=f.Source.Id;f.Source.SetupState=PluginSetupState.Ready;
            var root=JsonNode.Parse(f.Source.PackageVersion!.ManifestJson)!;root["version"]="0.1.1";
            var targetPackage=new AgentPackageVersion{Id=Guid.NewGuid(),PackageSourceId=f.Source.PackageVersion.PackageSourceId,
                AgentId=f.Source.PackageVersion.AgentId,AgentName=f.Source.PackageVersion.AgentName,PublisherId=f.Source.PackageVersion.PublisherId,
                Version="0.1.1",PluginKind=PluginKind.Connector,Status=AgentPackageVersionStatus.Built,PackageDigest=new string('c',64),ManifestJson=root.ToJsonString()};
            f.Target=new(){Id=Guid.NewGuid(),PackageVersion=targetPackage,InstallationKey=f.Source.Id,RevisionNumber=2,RevisionStatus=PluginRevisionStatus.Staged,
                SupersedesInstallationId=f.Source.Id,Scope=PluginInstallationScope.System,BusinessId="enterprise",IsEnabled=false,Grant=new(){Id=Guid.NewGuid()}};
            db.AgentInstallations.Add(f.Target);
            f.Account=new(){Id=Guid.NewGuid(),PluginInstallationId=f.Source.Id,State=PluginAccountState.Connected,IdentityKind=PluginAccountIdentityKind.ProviderSubject,
                VerifiedSubject="synthetic-subject",IdentityAuthority="https://api.example.com/identity",ProviderProfile="example.profile",DisplayName="Synthetic account"};
            db.PluginAccountGroups.Add(f.Account);var c=f.Connection;c.AccountGroupId=f.Account.Id;c.Status=PluginConnectionStatus.Connected;
            c.ExternalAccountId=f.Account.VerifiedSubject;c.GrantedScopesJson="[\"read\",\"identity\"]";c.CredentialProfileHash=new string('b',64);
            var resource=new PluginAccountResource{Id=Guid.NewGuid(),AccountGroupId=f.Account.Id,PluginInstallationId=f.Source.Id,ExternalResourceId="workspace-one",
                ResourceType="example.workspace",DisplayName="Synthetic workspace",IsAvailable=true,VerifiedAt=DateTimeOffset.UtcNow};db.PluginAccountResources.Add(resource);
            db.PluginAccountResourceAuthorizations.Add(new(){ResourceId=resource.Id,ConnectionId=c.Id,IsAvailable=true,VerifiedAt=DateTimeOffset.UtcNow});
            db.PluginAccountBusinessGrants.Add(new(){Id=Guid.NewGuid(),AccountGroupId=f.Account.Id,PluginInstallationId=f.Source.Id,OrganizationId=f.Inner.Business,
                Scope=PluginAccountGrantScope.SelectedResources,SelectedResourceIdsJson=JsonSerializer.Serialize(new[]{resource.Id}),CapabilitiesJson=JsonSerializer.Serialize(new[]{ConnectorPlanServiceTests.Fixture.Capability}),
                PackageDigest=f.Inner.Digest,PolicyVersion="v1",ApprovedByApplicationUserId=f.User});
            f.Original=new(){Id=Guid.NewGuid(),PluginInstallationId=f.Source.Id,AccountGroupId=f.Account.Id,ConnectionId=c.Id,ApplicationUserId=f.User,
                Status="Ready",PackageDigest=f.Inner.Digest,ProfileHash=c.CredentialProfileHash,PolicyVersion="v1",CreatedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(10)};
            db.PluginAccountSetupSessions.Add(f.Original);db.PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=f.Account.Id,
                ConsumerInstallationId=f.Source.Id,RecordId=f.Account.Id,Store="external:managed-service-backups",CreatedAt=DateTimeOffset.UtcNow});
            await db.SaveChangesAsync();f.Store=new(db,f.Protection);f.Gateway=new(f.Source.PackageVersion.ManifestJson,targetPackage.ManifestJson,c.Id,f.Original.Id);
            await f.Store.SetAsync(f.Source.Id,$"managed.context.{c.Id:N}",JsonSerializer.Serialize(new ManagedAccountAttempt(f.Gateway.SessionId,f.Gateway.RemoteId,new string('v',43),f.Gateway.Origin),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return f;
        }
    }
    internal sealed class Gateway(string before,string after,Guid local,Guid original):IManagedPluginConnectionGateway
    {
        public Guid RemoteId=Guid.NewGuid(),SessionId=Guid.NewGuid();public string Origin="https://managed.example";public string ProfileBinding=new('b',64);
        public int Adopts,Cancels,ProviderRevocationCalls,Discoveries;public bool LoseAcknowledgment,FailBeforeAdopt,AllowDiscovery;public Guid CancelledAttempt;public long SourceRevision=3;
        public ConnectionAdoptionReceipt? Saved;private string status="Ready";
        public bool UsesManaged(string profile)=>true;public string ReturnUri=>"https://customer.example/settings/plugins/managed-return";
        public Task<string> BoundServiceOrigin(CancellationToken ct)=>Task.FromResult(Origin);
        public Task<ProfileAvailability> Availability(string digest,CancellationToken ct)=>Task.FromResult(new ProfileAvailability("example.profile",1,ProfileBinding,"https://managed.example/oauth/callback/profile"));
        private static string Hash<T>(T value)=>CompatibleConnectionContract.Hash(JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        public Task<ConnectionAdoptionPreview> PreviewAdoption(Guid connection,ConnectionAdoptionRequest r,string origin,CancellationToken ct)
        {using var a=JsonDocument.Parse(before);using var b=JsonDocument.Parse(after);return Task.FromResult(new ConnectionAdoptionPreview(Hash(r),new string('d',64),CompatibleConnectionContract.Require(a.RootElement,b.RootElement,["base"]),SourceRevision,["base"]));}
        public async Task<ConnectionAdoptionReceipt> Adopt(Guid connection,ApproveConnectionAdoption a,string origin,CancellationToken ct)
        {
            Adopts++;if(FailBeforeAdopt)throw new HttpRequestException();var p=await PreviewAdoption(connection,a.Request,origin,ct);
            Saved=new(a.Request.AdoptionId,connection,Hash(a.Request),a.ReviewHash,p.CompatibilityHash,"Adopted",SourceRevision+1,"Ready",SourceRevision+1,a.Request.TargetInstallationId,a.Request.TargetPackageDigest);
            if(LoseAcknowledgment){LoseAcknowledgment=false;throw new HttpRequestException();}return Saved;
        }
        public Task<ConnectionAdoptionStatus> Adoption(Guid id,string origin,CancellationToken ct)=>Task.FromResult(new ConnectionAdoptionStatus(Saved is not null,Saved));
        public Task<ConnectionAdoptionReceipt> CancelAdoption(Guid connection,ConnectionAdoptionRequest r,string origin,CancellationToken ct)
        {Cancels++;Saved??=new(r.AdoptionId,connection,Hash(r),"","","Cancelled",3,"Ready",3,r.SourceInstallationId,r.SourcePackageDigest);return Task.FromResult(Saved);}
        public Task<ConnectionSnapshot> Snapshot(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)=>Task.FromResult(new ConnectionSnapshot(id,status,Saved?.CurrentRevision??SourceRevision,["base"]));
        public Task<ConnectionReceipt> CancelAttempt(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)
        {Assert.Equal(original,id);CancelledAttempt=id;ProviderRevocationCalls++;status="Revoked";return Task.FromResult(new ConnectionReceipt(SessionId,RemoteId,"Cancelled",null,[]));}
        public Task<ConnectionAttempt> Begin(BeginConnection r,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new InvalidOperationException("Upgrade must not start consent.");
        public Task<ConnectionReceipt> Cancel(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionSnapshot> Supersede(Guid id,Guid replacement,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionReceipt> Redeem(RedeemConnection r,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionReceipt> Recover(Guid id,RecoverConnection r,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionInventory> Discover(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)
        {if(!AllowDiscovery)throw new InvalidOperationException("Upgrade must not rediscover automatically.");Discoveries++;
            return Task.FromResult(new ConnectionInventory(id,"Ready",5,"synthetic-subject","Synthetic account","https://api.example.com/identity",[new("workspace-one","Synthetic workspace",null)],["base"]));}
        public Task<ConnectionSnapshot> Disconnect(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
    }
}
