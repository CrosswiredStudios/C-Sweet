using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using AgentWorkKind=CSweet.Domain.Setup.AgentWorkKind;

namespace CSweet.UnitTests;

public sealed class OperatorOnboardingRecoveryRelationalTests
{
    [Fact]
    public async Task DispatcherScanBeforeRecoveryCommitReloadsTheDeliveryUnderItsFence()
    {
        await using var f=await Harness.Create();var pause=new RecoveryPause();var scan=new ScanState();
        await using var db=f.Context(pause);var service=f.Service(db);var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        var recovery=service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dispatch=f.Dispatch(new ScanRead(scan),new ScanClosed(scan));
        try {await scan.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(dispatch.IsCompleted);}
        finally {pause.Release.TrySetResult();}
        var receipt=await recovery;await dispatch;
        await using var check=f.Context();var work=await check.AgentWorkItems.SingleAsync();
        Assert.Equal(AgentWorkStatus.Pending,work.Status);Assert.Equal(1,work.AttemptCount);Assert.Equal(2,work.MaximumAttempts);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Pending,(await check.AgentOnboardingEventOutbox.SingleAsync()).Status);
        Assert.Equal(receipt.RecoveryId,OnboardingRecoveryBudget.Read(await check.PluginOperationalStates.SingleAsync(),work).Receipt.RecoveryId);
        Assert.Single(await check.AgentWorkItems.ToArrayAsync());
    }

    private sealed class RecoveryPause:SaveChangesInterceptor
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {Entered.TrySetResult();await Release.Task.WaitAsync(ct);return result;}
    }
    private sealed class ScanState
    {
        public int CandidateRead;
        public TaskCompletionSource Completed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ScanRead(ScanState state):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,DataReaderClosingEventData data,InterceptionResult result)
        {
            if(command.CommandText.Contains("AgentOnboardingEventOutbox",StringComparison.Ordinal)&&command.CommandText.Contains("LIMIT",StringComparison.Ordinal))
                Interlocked.Exchange(ref state.CandidateRead,1);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ScanClosed(ScanState state):DbConnectionInterceptor
    {
        public override Task ConnectionClosedAsync(DbConnection connection,ConnectionEndEventData data)
        {if(Interlocked.Exchange(ref state.CandidateRead,0)==1)state.Completed.TrySetResult();return Task.CompletedTask;}
    }

    [IsolatedPostgresFact]
    public async Task PostgresAdvisoryFenceRejectsAcknowledgementCommittedByAnIndependentClient()
    {
        await using var f=await Harness.Create(postgres:true);await using var db=f.Context();var service=f.Service(db);
        var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        await using var external=new Npgsql.NpgsqlConnection(f.PostgreSqlConnection);await external.OpenAsync();
        await using var transaction=await external.BeginTransactionAsync();
        var key=$"agent-work-claim:{f.Org:D}:{f.Installation:D}";
        await using(var command=new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",external,transaction))
        {command.Parameters.AddWithValue("key",key);await command.ExecuteNonQueryAsync();}
        var recovery=service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default);
        await using var observer=new Npgsql.NpgsqlConnection(f.PostgreSqlConnection);await observer.OpenAsync();
        var waiting=false;
        for(var i=0;i<100&&!waiting;i++)
        {
            await using var inspect=new Npgsql.NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND wait_event='advisory')",observer);
            waiting=(bool)(await inspect.ExecuteScalarAsync())!;
            if(!waiting)await Task.Delay(50);
        }
        try
        {
            Assert.True(waiting,"The independent client must observe recovery waiting on PostgreSQL's advisory fence.");
            await using var acknowledge=new Npgsql.NpgsqlCommand("UPDATE \"AgentOnboardingEventOutbox\" SET \"Status\"='Delivered',\"DeliveredAt\"=CURRENT_TIMESTAMP WHERE \"Id\"=@id",external,transaction);
            acknowledge.Parameters.AddWithValue("id",f.EventId);Assert.Equal(1,await acknowledge.ExecuteNonQueryAsync());
        }
        finally {await transaction.CommitAsync();}
        var error=await Record.ExceptionAsync(async()=>await recovery.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.NotNull(error);
        Assert.True(error is InvalidOperationException || error is Npgsql.PostgresException {SqlState:"40001"} ||
            error is DbUpdateException {InnerException:Npgsql.PostgresException {SqlState:"40001"}},"A stale review must fail closed rather than queue work.");
        await using var check=f.Context();
        Assert.Equal(AgentOnboardingEventOutboxStatus.Delivered,(await check.AgentOnboardingEventOutbox.SingleAsync()).Status);
        Assert.Equal(AgentWorkStatus.DeadLetter,(await check.AgentWorkItems.SingleAsync()).Status);
        Assert.Empty(await check.PluginOperationalStates.ToArrayAsync());
        Assert.False(await check.AuditOutbox.AnyAsync(x=>x.RequestJson.Contains("agent.onboarding.recovery.requested")));
    }

    [IsolatedPostgresFact]
    public async Task PostgresRoundTripKeepsTheReceiptDeadlineAndAbsoluteSingleAttemptBudget()
    {
        await using var f=await Harness.Create(postgres:true);await using var db=f.Context();var service=f.Service(db);
        var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        var receipt=await service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default);
        await using var current=f.Context();var work=await current.AgentWorkItems.SingleAsync();
        Assert.Equal(receipt.DeadlineAt,work.DeadlineAt);Assert.Equal(0,receipt.DeadlineAt.Ticks%10);
        var runtime=new AgentRuntimeInstance{Id=Guid.NewGuid(),AgentInstallationId=f.Installation,TickId=Guid.NewGuid()};
        current.AgentRuntimeInstances.Add(runtime);await current.SaveChangesAsync();
        var installation=await current.AgentInstallations.SingleAsync();
        var session=new McpAgentSession{AgentInstallationId=f.Installation,OrganizationId=f.Org.ToString("D"),RuntimeInstanceId=runtime.Id,
            TickId=runtime.TickId,PackageVersionId=installation.PackageVersionId,GrantRevision=1};
        var inbox=new AgentWorkInbox(current,f.Protection,f.Clock);
        var claim=Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(session,default));Assert.Equal(2,claim.Attempt);
        await inbox.FailAsync(session,claim.WorkId,claim.Attempt,claim.LeaseToken,"agent-failure:v1;code=runtime.transport;retryable=true",default);
        Assert.Equal(AgentWorkStatus.DeadLetter,(await current.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2,(await current.AgentWorkItems.AsNoTracking().SingleAsync()).AttemptCount);
    }

    private sealed class IsolatedPostgresFactAttribute:FactAttribute
    {
        public IsolatedPostgresFactAttribute()
        {if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_ONBOARDING_RECOVERY_TEST_SETTINGS")))Skip="Requires the explicitly configured disposable loopback PostgreSQL fixture.";}
    }

    [Fact]
    public async Task CommitFailureRollsBackRealWorkReceiptAndAuditTogether()
    {
        await using var f=await Harness.Create();var fault=new CommitFailure();
        await using var db=f.Context(fault);var service=f.Service(db);
        var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        await using(var before=f.Context()) Assert.Empty(await before.PluginOperationalStates.ToArrayAsync());
        fault.Armed=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default));
        Assert.True(fault.ReachedCommit); Assert.NotEqual(Guid.Empty,fault.RecoveryAudit);
        await using var check=f.Context();
        Assert.Equal(AgentWorkStatus.DeadLetter,(await check.AgentWorkItems.SingleAsync()).Status);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Failed,(await check.AgentOnboardingEventOutbox.SingleAsync()).Status);
        Assert.Empty(await check.PluginOperationalStates.ToArrayAsync());
        Assert.False(await check.AuditOutbox.AnyAsync(x=>x.Id==fault.RecoveryAudit));
        Assert.Single(await check.AgentWorkAttempts.ToArrayAsync());
    }

    [Fact]
    public async Task IndependentRelationalRequestsPersistOneReceiptAndTheOriginalWork()
    {
        await using var f=await Harness.Create();await using var previewDb=f.Context();
        var review=await f.Service(previewDb).PreviewAsync(f.Actor,f.Org,f.Installation,default);
        await using var one=f.Context();await using var two=f.Context();
        var results=await Task.WhenAll(f.Service(one).RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default),
            f.Service(two).RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default));
        Assert.Equal(results[0],results[1]);
        await using var check=f.Context();Assert.Single(await check.PluginOperationalStates.ToArrayAsync());
        Assert.Single(await check.AgentWorkItems.ToArrayAsync());Assert.Single(await check.AgentWorkAttempts.ToArrayAsync());
        Assert.Single(await check.AuditOutbox.Where(x=>x.Id==results[0].RecoveryId).ToArrayAsync());
        Assert.Equal(f.WorkId,results[0].WorkId);
    }

    [Fact]
    public async Task AcknowledgementFromAnotherContextInvalidatesAnEarlierRelationalReview()
    {
        await using var f=await Harness.Create();await using var reviewDb=f.Context();var service=f.Service(reviewDb);
        var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        await f.Acknowledge();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default));
        await using var check=f.Context();Assert.Empty(await check.PluginOperationalStates.ToArrayAsync());
        Assert.Equal(AgentWorkStatus.DeadLetter,(await check.AgentWorkItems.SingleAsync()).Status);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Delivered,(await check.AgentOnboardingEventOutbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task RelationalLateAcknowledgementCancelsRatherThanCompletesTheUnclaimedRecovery()
    {
        await using var f=await Harness.Create();await using var db=f.Context();var service=f.Service(db);
        var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        var receipt=await service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default);
        await f.Acknowledge();await f.Acknowledge();
        await using var check=f.Context();var work=await check.AgentWorkItems.SingleAsync();
        Assert.Equal(AgentWorkStatus.Cancelled,work.Status);Assert.Equal(1,work.AttemptCount);
        Assert.Equal(receipt.RecoveryId,OnboardingRecoveryBudget.Read(await check.PluginOperationalStates.SingleAsync(),work).Receipt.RecoveryId);
        Assert.Single(await check.AgentWorkAttempts.ToArrayAsync());
    }

    [Fact]
    public async Task RelationalConcurrentCancellationCannotBeRequeuedUsingAStaleReview()
    {
        await using var f=await Harness.Create();await using var db=f.Context();var service=f.Service(db);
        var review=await service.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        await using(var other=f.Context())
            await other.AgentWorkItems.Where(x=>x.Id==f.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.Status,AgentWorkStatus.Cancelled));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default));
        await using var check=f.Context();Assert.Empty(await check.PluginOperationalStates.ToArrayAsync());
        Assert.Equal(AgentWorkStatus.Cancelled,(await check.AgentWorkItems.SingleAsync()).Status);
    }

    private sealed class CommitFailure:DbTransactionInterceptor
    {
        public bool Armed,ReachedCommit;public Guid RecoveryAudit;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,TransactionEventData data,
            InterceptionResult result,CancellationToken ct=default)
        {
            if(Armed)
            {
                ReachedCommit=true;
                RecoveryAudit=data.Context!.ChangeTracker.Entries<CSweet.Domain.Compute.ComputeAuditOutbox>()
                    .Select(x=>x.Entity.Id).FirstOrDefault();
                throw new InvalidOperationException("Synthetic commit failure after SQL writes.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SqliteClock:IQueryExpressionInterceptor
    {
        public System.Linq.Expressions.Expression QueryCompilationStarting(System.Linq.Expressions.Expression query,QueryExpressionEventData data)=>new ClockVisitor().Visit(query);
        private sealed class ClockVisitor:System.Linq.Expressions.ExpressionVisitor
        {
            protected override System.Linq.Expressions.Expression VisitMember(System.Linq.Expressions.MemberExpression node)
                =>node.Member.DeclaringType==typeof(DateTimeOffset)&&node.Member.Name==nameof(DateTimeOffset.UtcNow)
                    ?System.Linq.Expressions.Expression.Constant(DateTimeOffset.UtcNow):base.VisitMember(node);
        }
    }
    private sealed class Sequence:SaveChangesInterceptor
    {
        private long next=100000;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            // SQLite does not generate PostgreSQL's non-key sequence columns.
            // Preserve the real schema/constraints and supply only test-store generated values.
            foreach(var row in data.Context!.ChangeTracker.Entries().Where(x=>x.State==EntityState.Added))
            {
                var property=row.Metadata.FindProperty("Sequence");
                if(property?.ClrType==typeof(long) && (row.Property("Sequence").CurrentValue is null || (long)row.Property("Sequence").CurrentValue! == 0))
                    row.Property("Sequence").CurrentValue=Interlocked.Increment(ref next);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Harness:IAsyncDisposable
    {
        private string connectionString=$"Data Source=onboarding-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private bool postgres;
        private SqliteConnection? anchor;
        private readonly Sequence sequence=new();
        private static readonly SqliteClock sqliteClock=new();
        public Guid Actor=Guid.NewGuid(),Org=Guid.NewGuid(),Installation=Guid.NewGuid(),WorkId=Guid.NewGuid(),EventId=Guid.NewGuid();
        private Guid employee=Guid.NewGuid();private IDataProtectionProvider protection=new EphemeralDataProtectionProvider();
        private OperatorOnboardingRecoveryTests.Clock clock=new();
        public IDataProtectionProvider Protection=>protection;
        public TimeProvider Clock=>clock;
        public CSweetDbContext Context(params IInterceptor[] interceptors)
        {
            var builder=new DbContextOptionsBuilder<CSweetDbContext>();
            if(postgres) builder.UseNpgsql(connectionString);
            else builder.UseSqlite(connectionString).ReplaceService<IModelCustomizer,PluginAccountUpgradeTests.SqliteModelCustomizer>()
                .AddInterceptors(sqliteClock,sequence);
            return new(builder.AddInterceptors(interceptors).Options);
        }
        public string PostgreSqlConnection=>postgres?connectionString:throw new InvalidOperationException();
        public AgentOnboardingRecoveryService Service(CSweetDbContext db)=>new(db,protection,clock,ConnectorPlanServiceTests.Fixture.LocalCredentials(db));
        public async Task Dispatch(params IInterceptor[] interceptors)
        {
            var services=new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IDataProtectionProvider>(services,protection);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<TimeProvider>(services,clock);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<CSweetDbContext>(services,_=>Context(interceptors));
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<AgentWorkInbox>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<AgentWorkRouter>(services);
            await using var provider=Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            await new AgentOnboardingEventDispatcher(provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),clock,
                Microsoft.Extensions.Options.Options.Create(new AgentOnboardingDeliveryOptions()),Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentOnboardingEventDispatcher>.Instance).DispatchPendingAsync(default);
        }
        public async Task Acknowledge()
        {
            await using var db=Context();var handler=new AgentOnboardingCapabilityHandler(db,clock);
            var session=new AgentSession("synthetic","example.agent",Installation.ToString("D"),Org.ToString("D"),Guid.NewGuid().ToString(),"tick",
                new(new HashSet<string>(),new HashSet<string>(),new HashSet<string>{AgentLifecycleCapabilities.CompleteOnboarding},1));
            var request=new RequestCapability{RequestId="ack",Capability=AgentLifecycleCapabilities.CompleteOnboarding,
                Payload=JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new CompleteAgentOnboardingRequest(EventId),new JsonSerializerOptions(JsonSerializerDefaults.Web)))};
            await foreach(var result in handler.HandleAsync(session,request,default))Assert.True(result.Succeeded);
        }
        public static async Task<Harness> Create(bool postgres=false)
        {
            var f=new Harness{postgres=postgres};
            if(postgres)
            {
                var path=Environment.GetEnvironmentVariable("CSWEET_ONBOARDING_RECOVERY_TEST_SETTINGS")??throw new InvalidOperationException("Disposable fixture settings required.");
                var settings=new Npgsql.NpgsqlConnectionStringBuilder(await File.ReadAllTextAsync(path));
                if(settings.Host!="127.0.0.1"||settings.Port==5432||settings.Database!="csweet_onboarding_recovery_fixture"||settings.Username!="postgres"||settings.Pooling)
                    throw new InvalidOperationException("Only the isolated loopback PostgreSQL fixture is allowed.");
                settings.Database="csweet_onboarding_recovery_"+Guid.NewGuid().ToString("N");
                settings.ApplicationName="csweet-onboarding-recovery-test";
                f.connectionString=settings.ConnectionString;
            }
            else {f.anchor=new(f.connectionString);await f.anchor.OpenAsync();}
            await using var db=f.Context();
            await db.Database.EnsureCreatedAsync();var time=f.clock.GetUtcNow();var now=new DateTimeOffset(time.UtcTicks-time.UtcTicks%10,TimeSpan.Zero);
            var role=new IdentityRole<Guid>{Id=Guid.NewGuid(),Name=AuthenticationService.AdministratorRole};
            db.Users.Add(new ApplicationUser{Id=f.Actor,UserName="synthetic-owner"});db.Roles.Add(role);db.UserRoles.Add(new(){UserId=f.Actor,RoleId=role.Id});
            db.CoreOrganizations.Add(new(){Id=f.Org,Name="Synthetic organization",Status=OrganizationStatus.Active});
            var source=new AgentPackageSource{Id=Guid.NewGuid(),RepositoryUrl="https://github.com/example/synthetic",RepositoryOwner="example",RepositoryName="synthetic"};db.AgentPackageSources.Add(source);
            var json=JsonSerializer.Serialize(new PluginManifest{Id="example.agent",Kind="agent",Version="1.0.0",Requires=[new(){Name=AgentLifecycleCapabilities.CompleteOnboarding}]},new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var package=new AgentPackageVersion{Id=Guid.NewGuid(),PackageSourceId=source.Id,AgentId="example.agent",AgentName="Synthetic agent",Version="1.0.0",PluginKind=PluginKind.Agent,Status=AgentPackageVersionStatus.Built,
                ManifestJson=json,PackageDigest=new string('a',64),CommitSha=new string('a',40),ManifestDigest=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))};
            var installation=new AgentInstallation{Id=f.Installation,PackageVersion=package,BusinessId=f.Org.ToString("D"),Scope=PluginInstallationScope.Organization,SetupState=PluginSetupState.Ready,
                Grant=new(){Id=Guid.NewGuid(),GrantRevision=1,RequiredCapabilitiesJson=JsonSerializer.Serialize(new[]{AgentLifecycleCapabilities.CompleteOnboarding})}};db.AgentInstallations.Add(installation);
            var human=new OrganizationUser{Id=Guid.NewGuid(),OrganizationId=f.Org,ApplicationUserId=f.Actor,EmployeeType=EmployeeType.Human,PermissionLevel=OrganizationPermissionLevel.Owner};
            db.CoreOrganizationUsers.AddRange(human,new OrganizationUser{Id=f.employee,OrganizationId=f.Org,AgentInstallationId=f.Installation,EmployeeType=EmployeeType.Agent});
            var conversation=new Conversation{Id=Guid.NewGuid(),OrganizationId=f.Org,AgentOrganizationUserId=f.employee,InitiatedByOrganizationUserId=human.Id,Kind=ConversationKind.DirectHumanAgent};db.CoreConversations.Add(conversation);
            var item=new AgentOnboardingEventOutboxItem{Id=f.EventId,OrganizationId=f.Org,AgentOrganizationUserId=f.employee,HiringOrganizationUserId=human.Id,ConversationId=conversation.Id,Status=AgentOnboardingEventOutboxStatus.Failed,OccurredAt=now.AddHours(-2)};db.AgentOnboardingEventOutbox.Add(item);
            // Production serializes the persisted outbox after its database timestamp round trip.
            await db.SaveChangesAsync();await db.Entry(item).ReloadAsync();
            var payload=JsonSerializer.SerializeToUtf8Bytes(new AgentOnboardedEvent(f.Org,f.employee,human.Id,conversation.Id,item.OccurredAt),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            db.AgentWorkItems.Add(new(){Id=f.WorkId,AgentInstallationId=f.Installation,OrganizationId=f.Org.ToString("D"),Kind=AgentWorkKind.Event,Name=AgentLifecycleEvents.Onboarded,SourceType="platform-event",SourceId=f.EventId.ToString("D"),
                IdempotencyKey=$"{AgentOnboardingEventDispatcher.CreateDeliveryKey(f.EventId,package.Id)}:{f.Installation:D}",Status=AgentWorkStatus.DeadLetter,AttemptCount=1,MaximumAttempts=3,DeadlineAt=now.AddHours(-1),
                PayloadHash=Convert.ToHexString(SHA256.HashData(payload)),ProtectedPayload=f.protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(payload)});
            var runtime=new AgentRuntimeInstance{Id=Guid.NewGuid(),AgentInstallationId=f.Installation};runtime.TransitionTo(AgentRuntimeStatus.Cancelled,now.AddHours(-1));db.AgentRuntimeInstances.Add(runtime);
            db.AgentWorkAttempts.Add(new(){Id=Guid.NewGuid(),AgentWorkItemId=f.WorkId,RuntimeInstanceId=runtime.Id,Attempt=1,FinishedAt=now.AddHours(-1)});
            await db.SaveChangesAsync();return f;
        }
        public async ValueTask DisposeAsync()
        {
            if(postgres)
            {
                var settings=new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
                if(settings.Host!="127.0.0.1"||!settings.Database!.StartsWith("csweet_onboarding_recovery_",StringComparison.Ordinal)||settings.Database.EndsWith("fixture",StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to remove an unrecognized test database.");
                await using var db=Context();await db.Database.EnsureDeletedAsync();
            }
            if(anchor is not null)await anchor.DisposeAsync();
        }
    }
}
