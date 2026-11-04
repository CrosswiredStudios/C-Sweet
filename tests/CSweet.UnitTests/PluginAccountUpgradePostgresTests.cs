using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using F=CSweet.UnitTests.PluginAccountUpgradeTests.F;

namespace CSweet.UnitTests;

/// <summary>Opt-in tests for a disposable PostgreSQL instance with no production connection settings.</summary>
public sealed class PluginAccountUpgradePostgresTests
{
    private static string Settings=>Environment.GetEnvironmentVariable("CSWEET_ADOPTION_POSTGRES_TEST_FILE")
        ??throw new InvalidOperationException("An explicit isolated PostgreSQL fixture is required.");

    [AdoptionPostgresFact]
    public async Task RealMigrationGuardsRejectInvalidInsertAuthorityRewriteDeleteTruncateAndEvidenceLosingRollback()
    {
        await using var f=await F.Postgres(Settings);f.Gateway.FailBeforeAdopt=true;
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        var pending=await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);Assert.Equal("Pending",pending.Status);
        const string columns="\"SourceInstallationId\",\"TargetInstallationId\",\"AccountGroupId\",\"ConnectionId\",\"OriginalSetupSessionId\",\"ApplicationUserId\",\"SourceSnapshotHash\",\"ServiceOrigin\",\"RemoteConnectionId\",\"ServiceRequestJson\",\"ServiceReviewHash\",\"CompatibilityHash\"";
        string Clone(string status)=>$"INSERT INTO \"PluginAccountUpgrades\" (\"Id\",{columns},\"Status\",\"Revision\",\"CreatedAt\",\"CompletedAt\") SELECT gen_random_uuid(),{columns},'{status}',1,\"CreatedAt\",NULL FROM \"PluginAccountUpgrades\" LIMIT 1";
        await SqlReject(f.Db,Clone("Completed"),"P0001");
        await SqlReject(f.Db,Clone("Pending"),"23505");
        await SqlReject(f.Db,"UPDATE \"PluginAccountUpgrades\" SET \"ServiceOrigin\"='https://changed.example'","P0001");
        await SqlReject(f.Db,"DELETE FROM \"PluginAccountUpgrades\"","P0001");
        await SqlReject(f.Db,"TRUNCATE \"PluginAccountUpgrades\"","P0001");
        await SqlReject(f.Db,"UPDATE \"PluginAccountUpgrades\" SET \"Status\"='Completed',\"CompletedAt\"=CURRENT_TIMESTAMP","P0001");
        Assert.Equal("Cancelled",(await f.Service.CancelAsync(f.User,f.Target.Id,pending.Id,default)).Status);
        await SqlReject(f.Db,"UPDATE \"PluginAccountUpgrades\" SET \"Status\"='Pending',\"Revision\"=\"Revision\"+1","P0001");
        var versions=f.Db.Database.GetMigrations().ToArray();var index=Array.FindIndex(versions,x=>x.EndsWith("_AddCompatiblePluginAccountUpgrades",StringComparison.Ordinal));
        var down=f.Db.GetService<IMigrator>().GenerateScript(versions[index],versions[index-1]);
        await using(var connection=new NpgsqlConnection(f.Db.Database.GetConnectionString()))
        {
            await connection.OpenAsync();await using var command=new NpgsqlCommand(down,connection);
            var rejected=await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteNonQueryAsync());Assert.Equal("P0001",rejected.SqlState);
            Assert.Contains("Retain compatible upgrade intent",rejected.MessageText);
        }
        Assert.Single(await f.Db.PluginAccountUpgrades.AsNoTracking().ToArrayAsync());Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
    }

    [AdoptionPostgresFact]
    public async Task RealPostgresCompletionMovesTheSameNativeRecordsAndProtectedReferenceThroughTerminalTrigger()
    {
        await using var f=await F.Postgres(Settings);var original=await f.Db.PluginSecrets.AsNoTracking().SingleAsync();
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        Assert.Equal("Completed",(await f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default)).Status);
        Assert.Equal(f.Target.Id,(await f.Db.PluginAccountGroups.AsNoTracking().SingleAsync()).PluginInstallationId);
        var moved=await f.Db.PluginSecrets.AsNoTracking().SingleAsync();Assert.Equal(original.Id,moved.Id);Assert.Equal(original.ProtectedValue,moved.ProtectedValue);
        Assert.Equal(f.Target.Id,moved.PluginInstallationId);Assert.Equal(f.Source.Id,(await f.Db.PluginAccountSetupSessions.AsNoTracking().SingleAsync()).PluginInstallationId);
        Assert.Equal(1,f.Gateway.Adopts);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
        await SqlReject(f.Db,"UPDATE \"PluginAccountUpgrades\" SET \"Revision\"=\"Revision\"+1","P0001");
    }

    [AdoptionPostgresFact]
    public async Task ConcurrentReviewedApprovalsSerializeOnTheSourceAndOnlyOneDurableIntentCanProceed()
    {
        var barrier=new SaveBarrier(final:false);await using var f=await F.Postgres(Settings,barrier);
        var one=await f.Service.ReviewAsync(f.User,f.Target.Id,default);var two=await f.Service.ReviewAsync(f.User,f.Target.Id,default);
        f.Gateway.FailBeforeAdopt=true;await using var other=Other(f,"csweet-adoption-contender");
        var service=Upgrade(f,other);barrier.Armed=true;
        var first=f.Service.ApproveAsync(f.User,f.Target.Id,new(one.ReviewToken),default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second=Record.ExceptionAsync(()=>service.ApproveAsync(f.User,f.Target.Id,new(two.ReviewToken),default));
        try{await WaitForLock(f,"csweet-adoption-contender");}finally{barrier.Release.TrySetResult();}
        Assert.Equal("Pending",(await first.WaitAsync(TimeSpan.FromSeconds(20))).Status);
        var rejected=await second.WaitAsync(TimeSpan.FromSeconds(20));Assert.NotNull(rejected);
        Assert.True(rejected is InvalidOperationException or DbUpdateException, "Raw PostgreSQL concurrency escaped the application boundary; SQLSTATE="+(rejected is PostgresException pg?pg.SqlState:"none"));
        Assert.Single(await f.Db.PluginAccountUpgrades.AsNoTracking().ToArrayAsync());Assert.Equal(1,f.Gateway.Adopts);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);
    }

    [AdoptionPostgresFact]
    public async Task ConcurrentNormalResumeWaitsForTheSourceLockThenObservesTheCommittedPendingFence()
    {
        var barrier=new SaveBarrier(final:false);await using var f=await F.Postgres(Settings,barrier);f.Gateway.FailBeforeAdopt=true;
        await using var other=Other(f,"csweet-adoption-normal-resume");var controls=Accounts(f,other);
        var impact=await controls.GetInstallationImpactAsync(f.User,f.Source.Id,default);
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);barrier.Armed=true;
        var first=f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var resume=Record.ExceptionAsync(()=>controls.ControlInstallationAsync(f.User,f.Source.Id,new("Resume",impact.ReviewHash,1),default));
        try{await WaitForLock(f,"csweet-adoption-normal-resume");}finally{barrier.Release.TrySetResult();}
        Assert.Equal("Pending",(await first.WaitAsync(TimeSpan.FromSeconds(20))).Status);
        var rejected=await resume.WaitAsync(TimeSpan.FromSeconds(20));var invalid=Assert.IsType<InvalidOperationException>(rejected);
        Assert.Contains("compatible account upgrade is pending",invalid.Message);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Source.Id)).IsEnabled);
        Assert.Empty(await f.Db.PluginAccountAdministrations.AsNoTracking().ToArrayAsync());Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
    }

    [AdoptionPostgresFact]
    public async Task ConcurrentBusinessRevocationAbortsNativeFinalizationAndCannotBeRestoredByReconciliation()
    {
        var barrier=new SaveBarrier(final:true);await using var f=await F.Postgres(Settings,barrier);
        var review=await f.Service.ReviewAsync(f.User,f.Target.Id,default);barrier.Armed=true;
        var adoption=f.Service.ApproveAsync(f.User,f.Target.Id,new(review.ReviewToken),default);await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var other=Other(f,"csweet-adoption-revoker");var grant=await other.PluginAccountBusinessGrants.SingleAsync();
            grant.RevokedAt=DateTimeOffset.UtcNow;grant.Revision++;await other.SaveChangesAsync();
        }
        finally{barrier.Release.TrySetResult();}
        var pending=await adoption.WaitAsync(TimeSpan.FromSeconds(20));Assert.Equal("Pending",pending.Status);
        Assert.Equal(f.Source.Id,(await f.Db.PluginSecrets.AsNoTracking().SingleAsync()).PluginInstallationId);
        Assert.Equal(f.Source.Id,(await f.Db.PluginAccountGroups.AsNoTracking().SingleAsync()).PluginInstallationId);
        Assert.NotNull((await f.Db.PluginAccountBusinessGrants.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Equal("Pending",(await f.Service.ResumeAsync(f.User,f.Target.Id,pending.Id,default)).Status);Assert.Equal(1,f.Gateway.Adopts);
        Assert.False((await f.Db.AgentInstallations.AsNoTracking().SingleAsync(x=>x.Id==f.Target.Id)).IsEnabled);Assert.Equal(0,f.Gateway.ProviderRevocationCalls);
    }

    private static CSweetDbContext Other(F f,string name)
    {var settings=new NpgsqlConnectionStringBuilder(f.Db.Database.GetConnectionString()){ApplicationName=name};return new(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options);}
    private static PluginAccountUpgradeService Upgrade(F f,CSweetDbContext db)
    {var store=new DataProtectionPluginSecretStore(db,f.Protection);return new(db,store,store,f.Gateway,f.Protection,new TestAuditEventWriter());}
    private static PluginAccountService Accounts(F f,CSweetDbContext db)
    {var store=new DataProtectionPluginSecretStore(db,f.Protection);return new(db,store,f.Protection,f.Inner.Http,f.Inner.Profiles,f.Inner.Transport,Options.Create(f.Inner.ConnectionOptions),new TestAuditEventWriter(),managed:f.Gateway);}
    private static async Task SqlReject(CSweetDbContext db,string sql,string expected)
    {var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(sql));Assert.Equal(expected,error.SqlState);}
    private static async Task WaitForLock(F f,string name)
    {
        await using var connection=new NpgsqlConnection(f.Db.Database.GetConnectionString());await connection.OpenAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while(!timeout.IsCancellationRequested)
        {
            await using var command=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock')",connection);
            command.Parameters.AddWithValue("name",name);if((bool)(await command.ExecuteScalarAsync(timeout.Token))!)return;
            await Task.Delay(40,timeout.Token);
        }
        throw new TimeoutException("The isolated contender never reached the expected PostgreSQL row lock.");
    }
    private sealed class SaveBarrier(bool final):SaveChangesInterceptor
    {
        public bool Armed;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(Armed && data.Context!.ChangeTracker.Entries<PluginAccountUpgrade>().Any(x=>final?x.State==EntityState.Modified && x.Entity.Status=="Completed":x.State==EntityState.Added))
            {Armed=false;Assert.NotNull(data.Context.Database.CurrentTransaction);Entered.TrySetResult();await Release.Task.WaitAsync(TimeSpan.FromSeconds(15),ct);}
            return result;
        }
    }
}

public sealed class AdoptionPostgresFactAttribute:FactAttribute
{
    public AdoptionPostgresFactAttribute()
    {if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CSWEET_ADOPTION_POSTGRES_TEST_FILE")))Skip="Requires an explicitly provisioned, disposable network-isolated PostgreSQL fixture.";}
}
