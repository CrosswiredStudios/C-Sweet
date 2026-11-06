using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;
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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class OperatorOnboardingRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExplicitRecoveryPreservesIdentityEvidenceAndCreatesOneAtomicReceiptAndAudit()
    {
        await using var f = await Fixture.Create();
        var before = await f.Work();
        var review = await f.Preview();
        Assert.Equal(2, review.PermittedNextAttempt);
        Assert.Empty(await f.Db.PluginOperationalStates.ToArrayAsync());
        var receipt = await f.Recover(review);
        var after = await f.Work();
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.IdempotencyKey, after.IdempotencyKey);
        Assert.Equal(before.SourceId, after.SourceId); Assert.Equal(before.CorrelationId, after.CorrelationId);
        Assert.Equal(before.ProtectedPayload, after.ProtectedPayload); Assert.Equal(before.PayloadHash, after.PayloadHash);
        Assert.Equal(before.ProtectedResult, after.ProtectedResult); Assert.Equal(before.ResultHash, after.ResultHash);
        Assert.Equal(before.LastError, after.LastError); Assert.Equal(before.AttemptCount, after.AttemptCount);
        Assert.Equal(AgentWorkStatus.Pending, after.Status); Assert.Equal(receipt.RequestedAt.AddHours(1), after.DeadlineAt);
        Assert.Equal(2, after.MaximumAttempts);
        Assert.Single(await f.Db.AgentWorkItems.ToArrayAsync()); Assert.Single(await f.Db.AgentWorkAttempts.ToArrayAsync());
        Assert.Single(await f.Db.AgentOnboardingEventOutbox.ToArrayAsync());
        var marker = Assert.Single(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
        Assert.Equal(receipt, OnboardingRecoveryBudget.Read(marker, after).Receipt);
        var audit = await f.Db.AuditOutbox.AsNoTracking().SingleAsync(x=>x.Id==receipt.RecoveryId);
        Assert.Equal(receipt.RecoveryId, audit.Id);
        Assert.DoesNotContain(before.LastError!, audit.RequestJson);
        Assert.DoesNotContain(Convert.ToBase64String(before.ProtectedPayload), audit.RequestJson);
        Assert.Equal(receipt, await f.Service.ReadReceiptAsync(f.Actor, f.Org, f.Installation, default));
    }

    [Fact]
    public async Task ConcurrentAndUncertainSubmissionsReturnOneReceiptWithoutAnotherAttempt()
    {
        await using var f = await Fixture.Create(); var review = await f.Preview();
        await using var other = f.Context();
        var results = await Task.WhenAll(f.Recover(review), new AgentOnboardingRecoveryService(other, f.Protection, f.Clock, ConnectorPlanServiceTests.Fixture.LocalCredentials(other))
            .RecoverAsync(f.Actor, f.Org, f.Installation, review.ReviewToken, default));
        Assert.Equal(results[0], results[1]); Assert.Equal(results[0], await f.Recover(review));
        Assert.Single(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.AuditOutbox.AsNoTracking().Where(x=>x.Id==results[0].RecoveryId).ToArrayAsync());
        Assert.Single(await f.Db.AgentWorkItems.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, (await f.Work()).AttemptCount);
    }

    [Fact]
    public async Task LaterExplicitRecoveryDoesNotReplaceTheEarlierReviewedAuthorityAudit()
    {
        await using var f=await Fixture.Create();var first=await f.Recover(await f.Preview());
        var marker=await f.Db.PluginOperationalStates.AsNoTracking().SingleAsync();
        var reviewedHash=OnboardingRecoveryBudget.Read(marker,await f.Work()).ReviewedStateHash;
        var oldAudit=await f.Db.AuditOutbox.AsNoTracking().SingleAsync(x=>x.Id==first.RecoveryId);
        var originalAuditJson=oldAudit.RequestJson;
        var request=JsonSerializer.Deserialize<CSweet.Application.Setup.AuditEventWriteRequest>(originalAuditJson)!;
        using(var metadata=JsonDocument.Parse(request.MetadataJson!))
        {
            Assert.Equal(reviewedHash,metadata.RootElement.GetProperty("reviewedStateHash").GetString());
            Assert.Equal(f.Package,metadata.RootElement.GetProperty("packageVersionId").GetGuid());
            Assert.Equal(1,metadata.RootElement.GetProperty("grantRevision").GetInt64());
            Assert.Equal(64,metadata.RootElement.GetProperty("packageDigest").GetString()!.Length);
        }
        await f.Change(async db=>
        {
            var work=await db.AgentWorkItems.SingleAsync();work.Status=AgentWorkStatus.DeadLetter;work.AttemptCount=2;
            var item=await db.AgentOnboardingEventOutbox.SingleAsync();item.Status=AgentOnboardingEventOutboxStatus.Failed;
            db.AgentWorkAttempts.Add(new(){Id=Guid.NewGuid(),AgentWorkItemId=f.WorkId,RuntimeInstanceId=Guid.NewGuid(),Attempt=2,
                FinishedAt=f.Clock.GetUtcNow(),CompletionHash="retained-second-attempt"});
        });
        // Each HTTP request owns a fresh scope; do not bypass the existing account-copy
        // concurrency fences by carrying inventory entities across independent requests.
        await using var nextRequest=f.Context();
        var nextService=new AgentOnboardingRecoveryService(nextRequest,f.Protection,f.Clock,ConnectorPlanServiceTests.Fixture.LocalCredentials(nextRequest));
        var nextReview=await nextService.PreviewAsync(f.Actor,f.Org,f.Installation,default);
        var second=await nextService.RecoverAsync(f.Actor,f.Org,f.Installation,nextReview.ReviewToken,default);
        Assert.NotEqual(first.RecoveryId,second.RecoveryId);
        Assert.Equal(first.WorkId,second.WorkId);Assert.Equal(3,second.PermittedAttempt);
        var currentMarker=await f.Db.PluginOperationalStates.AsNoTracking().SingleAsync();Assert.Equal(2,currentMarker.Revision);
        Assert.Equal(second.RecoveryId,OnboardingRecoveryBudget.Read(currentMarker,await f.Work()).Receipt.RecoveryId);
        Assert.Equal(originalAuditJson,(await f.Db.AuditOutbox.AsNoTracking().SingleAsync(x=>x.Id==first.RecoveryId)).RequestJson);
        Assert.Single(await f.Db.AuditOutbox.AsNoTracking().Where(x=>x.Id==second.RecoveryId).ToArrayAsync());
        Assert.Equal(2,await f.Db.AgentWorkAttempts.CountAsync());
    }

    [Fact]
    public async Task CallerPendingEditsAreRejectedWithoutSavingOrDiscardingThem()
    {
        await using var f=await Fixture.Create();var review=await f.Preview();
        var work=await f.Db.AgentWorkItems.SingleAsync();var original=work.CorrelationId;work.CorrelationId="pending-caller-edit";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Recover(review));
        Assert.Equal("pending-caller-edit",work.CorrelationId);
        Assert.Equal(EntityState.Modified,f.Db.Entry(work).State);
        Assert.Equal(original,(await f.Work()).CorrelationId);
        Assert.Empty(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("locked-user")]
    [InlineData("non-admin")]
    [InlineData("non-manager")]
    [InlineData("inactive-human")]
    [InlineData("business-inactive")]
    [InlineData("inactive-agent")]
    [InlineData("wrong-business")]
    [InlineData("disabled")]
    [InlineData("retired")]
    [InlineData("setup-incomplete")]
    [InlineData("package-unbuilt")]
    [InlineData("scope-user")]
    [InlineData("consumer-grant")]
    [InlineData("binding-revision")]
    [InlineData("business-grant")]
    [InlineData("connection-scope")]
    [InlineData("resource-unavailable")]
    public async Task CurrentAuthorityIsRecheckedBeforeRecovery(string change)
    {
        await using var f = await Fixture.Create(); var review = await f.Preview();
        await f.Change(async db =>
        {
            switch (change)
            {
                case "locked-user": var user = await db.Users.SingleAsync(); user.LockoutEnabled = true; user.LockoutEnd = f.Clock.GetUtcNow().AddDays(1); break;
                case "non-admin": (await db.Roles.SingleAsync()).Name = "Synthetic non-administrator"; break;
                case "non-manager": (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Human)).PermissionLevel = OrganizationPermissionLevel.Contributor; break;
                case "business-inactive": (await db.CoreOrganizations.SingleAsync(x=>x.Id==f.Org)).Status=OrganizationStatus.Draft; break;
                case "inactive-human": (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Human)).IsActive = false; break;
                case "inactive-agent": (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Employee)).IsActive = false; break;
                case "wrong-business": (await db.AgentInstallations.SingleAsync(x => x.Id == f.Installation)).BusinessId = Guid.NewGuid().ToString("D"); break;
                case "disabled": (await db.AgentInstallations.SingleAsync(x => x.Id == f.Installation)).IsEnabled = false; break;
                case "retired": (await db.AgentInstallations.SingleAsync(x => x.Id == f.Installation)).RevisionStatus = PluginRevisionStatus.Retired; break;
                case "setup-incomplete": (await db.AgentInstallations.SingleAsync(x => x.Id == f.Installation)).SetupState = PluginSetupState.NeedsSetup; break;
                case "package-unbuilt": (await db.AgentPackageVersions.SingleAsync(x => x.Id == f.Package)).Status = AgentPackageVersionStatus.Failed; break;
                case "scope-user": (await db.AgentInstallations.SingleAsync(x => x.Id == f.Installation)).Scope = PluginInstallationScope.User; break;
                case "consumer-grant": (await db.AgentInstallationGrants.SingleAsync(x => x.AgentInstallationId == f.Installation)).GrantRevision++; break;
                case "binding-revision": (await db.AgentCapabilityBindings.SingleAsync()).GrantRevision++; break;
                case "business-grant": (await db.PluginAccountBusinessGrants.SingleAsync()).RevokedAt = f.Clock.GetUtcNow(); break;
                case "connection-scope": (await db.PluginConnections.SingleAsync()).GrantedScopesJson = "[]"; break;
                case "resource-unavailable": (await db.PluginAccountResources.SingleAsync()).IsAvailable = false; break;
            }
        });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Recover(review));
        Assert.Equal(AgentWorkStatus.DeadLetter, (await f.Work()).Status);
        Assert.Empty(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("event-cancelled")]
    [InlineData("ack-time")]
    [InlineData("pending")]
    [InlineData("leased")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("empty-payload")]
    [InlineData("altered-payload")]
    [InlineData("delivery-key")]
    [InlineData("source")]
    [InlineData("unfinished-expired-attempt")]
    [InlineData("unfinished-active-attempt")]
    [InlineData("attempt-changed")]
    [InlineData("package-changed")]
    public async Task TerminalIdentityAcknowledgementAndReviewFencesRejectChangedState(string change)
    {
        await using var f = await Fixture.Create(); var review = await f.Preview();
        await f.Change(async db =>
        {
            var work = await db.AgentWorkItems.SingleAsync(); var item = await db.AgentOnboardingEventOutbox.SingleAsync();
            switch (change)
            {
                case "delivered": item.Status = AgentOnboardingEventOutboxStatus.Delivered; break;
                case "event-cancelled": item.Status = AgentOnboardingEventOutboxStatus.Cancelled; break;
                case "ack-time": item.DeliveredAt = f.Clock.GetUtcNow(); break;
                case "pending": work.Status = AgentWorkStatus.Pending; break;
                case "leased": work.Status = AgentWorkStatus.Leased; break;
                case "completed": work.Status = AgentWorkStatus.Completed; break;
                case "cancelled": work.Status = AgentWorkStatus.Cancelled; break;
                case "erased": work.MemoryErasedAt = f.Clock.GetUtcNow(); break;
                case "empty-payload": work.ProtectedPayload = []; break;
                case "altered-payload": work.PayloadHash = new string('B',64); break;
                case "delivery-key": work.IdempotencyKey += "other"; break;
                case "source": work.SourceType = "caller-event"; break;
                case "unfinished-expired-attempt": (await db.AgentWorkAttempts.SingleAsync()).FinishedAt = null; break;
                case "unfinished-active-attempt": var a = await db.AgentWorkAttempts.SingleAsync(); a.FinishedAt = null; a.LeaseExpiresAt = f.Clock.GetUtcNow().AddMinutes(2); break;
                case "attempt-changed": work.AttemptCount++; break;
                case "package-changed": (await db.AgentInstallations.SingleAsync(x => x.Id == f.Installation)).PackageVersionId = Guid.NewGuid(); break;
            }
        });
        var auditCount=await f.Db.AuditOutbox.CountAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Recover(review));
        Assert.Empty(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
        Assert.Equal(auditCount,await f.Db.AuditOutbox.CountAsync());
    }

    [Fact]
    public async Task ReviewIsBoundToActorBusinessInstallationExpiryAndContent()
    {
        await using var f = await Fixture.Create(); var review = await f.Preview();
        foreach (var token in new[] { "malformed", review.ReviewToken + "x", new string('x',12001) })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RecoverAsync(f.Actor, f.Org, f.Installation, token, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RecoverAsync(Guid.NewGuid(), f.Org, f.Installation, review.ReviewToken, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RecoverAsync(f.Actor, Guid.NewGuid(), f.Installation, review.ReviewToken, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RecoverAsync(f.Actor, f.Org, Guid.NewGuid(), review.ReviewToken, default));
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recover(review));
    }

    [Fact]
    public async Task AcknowledgementBeforeOrAfterRecoveryCannotProduceDuplicateDelivery()
    {
        await using var before = await Fixture.Create(); var oldReview = await before.Preview();
        Assert.True(await before.Acknowledge());
        await Assert.ThrowsAsync<InvalidOperationException>(() => before.Recover(oldReview));
        await using var after = await Fixture.Create(); await after.Recover(await after.Preview());
        Assert.True(await after.Acknowledge()); Assert.True(await after.Acknowledge());
        Assert.Equal(AgentWorkStatus.Cancelled, (await after.Work()).Status);
        var inbox = new AgentWorkInbox(after.Db, after.Protection, after.Clock);
        Assert.Null(await inbox.ClaimAsync(after.Session(), default));
        Assert.Single(await after.Db.AgentWorkItems.AsNoTracking().ToArrayAsync());
        Assert.Single(await after.Db.AgentWorkAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(AgentOnboardingEventOutboxStatus.Delivered, (await after.Event()).Status);
    }

    [Fact]
    public async Task OneAdditionalClaimIsBoundedDespiteResourceCountersAndRetryableFailure()
    {
        await using var f = await Fixture.Create(); await f.Recover(await f.Preview());
        var inbox = new AgentWorkInbox(f.Db, f.Protection, f.Clock); var session = f.Session();
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(session, default));
        Assert.Equal(2, claim.Attempt); Assert.Equal(f.WorkId, claim.WorkId);
        var work = await f.Db.AgentWorkItems.SingleAsync(); work.ResourceReplacementCount = 1;
        await f.Db.SaveChangesAsync();
        await inbox.FailAsync(session, claim.WorkId, claim.Attempt, claim.LeaseToken,
            "agent-failure:v1;code=runtime.transport;retryable=true", default);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await inbox.ClaimAsync(session, default));
        var final = await f.Work(); Assert.Equal(2, final.AttemptCount); Assert.Equal(AgentWorkStatus.DeadLetter, final.Status);
        var item = await f.Event();
        AgentOnboardingEventDispatcher.ReconcileDelivery(item, final, f.Clock.GetUtcNow(),12,operatorRecovery:true);
        Assert.Equal(AgentWorkStatus.DeadLetter, final.Status); Assert.Equal(2, final.MaximumAttempts);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Failed, item.Status);
    }

    [Fact]
    public async Task AFailedSaveCannotPublishTheReceiptOrAuditOrQueueWork()
    {
        await using var f = await Fixture.Create(); var review = await f.Preview();
        var auditCount=await f.Db.AuditOutbox.CountAsync();
        await using var failing = f.Context(new RejectSave());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentOnboardingRecoveryService(failing, f.Protection, f.Clock, ConnectorPlanServiceTests.Fixture.LocalCredentials(failing))
            .RecoverAsync(f.Actor, f.Org, f.Installation, review.ReviewToken, default));
        Assert.Equal(AgentWorkStatus.DeadLetter, (await f.Work()).Status);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Failed, (await f.Event()).Status);
        Assert.Empty(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
        Assert.Equal(auditCount,await f.Db.AuditOutbox.CountAsync());
    }

    [Fact]
    public async Task DispatcherUsesTheRecoveredPendingStateWithoutResettingTheBudget()
    {
        await using var f = await Fixture.Create(); await f.Recover(await f.Preview());
        await f.Dispatch();
        Assert.Equal(AgentWorkStatus.Pending, (await f.Work()).Status);
        Assert.Equal(2, (await f.Work()).MaximumAttempts);
        Assert.Single(await f.Db.AgentWorkItems.AsNoTracking().ToArrayAsync());
        await f.Change(async db => { var work = await db.AgentWorkItems.SingleAsync(); work.Status=AgentWorkStatus.DeadLetter; work.AttemptCount=2; work.LastError="agent-failure:v1;code=runtime.transport;retryable=true"; });
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Dispatch();
        Assert.Equal(AgentWorkStatus.DeadLetter,(await f.Work()).Status);
        Assert.Equal(2,(await f.Work()).MaximumAttempts);
    }

    [Fact]
    public async Task ErasedPayloadIsRejectedWithoutRestoringItsProtectedFields()
    {
        await using var f=await Fixture.Create();var work=await f.Work();work.MemoryErasedAt=f.Clock.GetUtcNow();
        Assert.Throws<InvalidOperationException>(()=>AgentOnboardingRecoveryService.RequireRecoverablePayload(work));
        Assert.Empty(await f.Db.PluginOperationalStates.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task RecoveryDeadlineUsesPostgresPrecisionForSubMicrosecondClocks()
    {
        await using var f=await Fixture.Create();f.Clock.Advance(TimeSpan.FromTicks(1));
        var receipt=await f.Recover(await f.Preview());
        Assert.Equal(0,receipt.RequestedAt.Ticks%10);Assert.Equal(0,receipt.DeadlineAt.Ticks%10);
        Assert.Equal(TimeSpan.FromHours(1),receipt.DeadlineAt-receipt.RequestedAt);
    }

    private sealed class RejectSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken=default) => throw new InvalidOperationException("Synthetic save failure.");
    }

    [Fact]
    public async Task ConcurrentAcknowledgementWaitsForRecoveryCommitThenCancelsItsUnclaimedWork()
    {
        await using var f = await Fixture.Create(); var review = await f.Preview(); var barrier = new PauseSave();
        await using var recoveringDb = f.Context(barrier);
        var recover = new AgentOnboardingRecoveryService(recoveringDb, f.Protection, f.Clock, ConnectorPlanServiceTests.Fixture.LocalCredentials(recoveringDb))
            .RecoverAsync(f.Actor,f.Org,f.Installation,review.ReviewToken,default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var acknowledgement = f.Acknowledge();
        try { Assert.False(acknowledgement.IsCompleted); }
        finally { barrier.Release.TrySetResult(); }
        await recover; Assert.True(await acknowledgement);
        Assert.Equal(AgentWorkStatus.Cancelled,(await f.Work()).Status);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Delivered,(await f.Event()).Status);
        Assert.Single(await f.Db.AgentWorkItems.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task DispatcherFailurePersistsSanitizedBackoffWithoutRequeueingWork()
    {
        await using var f = await Fixture.Create(); await f.Recover(await f.Preview());
        await f.Change(async db => (await db.PluginOperationalStates.SingleAsync()).PayloadJson="untrusted-secret-canary");
        var before=await f.Event(); await f.Dispatch(); var after=await f.Event();
        Assert.Equal(before.Attempts+1,after.Attempts);
        Assert.True(after.NextAttemptAt>f.Clock.GetUtcNow());
        Assert.DoesNotContain("canary",after.LastError!);
        var count=after.Attempts;await f.Dispatch();
        Assert.Equal(count,(await f.Event()).Attempts);
        Assert.Equal(1,(await f.Work()).AttemptCount);
    }

    [Fact]
    public async Task OperatorRoutesRequireAdministrationAndExposeOnlyReadReviewReceiptAndOnePost()
    {
        var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions {EnvironmentName="Testing"});
        builder.Services.AddAuthorization();builder.Services.AddScoped<AgentOnboardingRecoveryService>();
        await using var app=builder.Build();
        CSweet.Api.Agents.AgentOnboardingRecoveryEndpoints.MapAgentOnboardingRecoveryEndpoints(app);
        var routes=((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(x=>x.Endpoints).ToArray();
        Assert.Equal(3,routes.Length);
        foreach(var route in routes)
            Assert.Contains(route.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>(),x=>x.Policy=="PluginAdministration");
        Assert.Single(routes.Where(x=>x.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("POST")));
    }

    private sealed class PauseSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);return result;
        }
    }
    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan amount)=>now+=amount;
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        private PluginAccountAuthorityTests.Fixture native=null!;
        public PluginAccountAuthorityTests.Fixture Native => native;
        public ConnectorCredentialAuthority? Credentials { get; set; }
        private CSweetDbContext? operational;
        public CSweetDbContext Db=>operational??native.Legacy.Db;
        public Clock Clock {get;}=new();
        public IDataProtectionProvider Protection {get;}=new EphemeralDataProtectionProvider();
        public Guid Actor {get;}=Guid.NewGuid(); public Guid Human {get;}=Guid.NewGuid(); public Guid Employee {get;}=Guid.NewGuid();
        public Guid WorkId {get;}=Guid.NewGuid(); public Guid EventId {get;}=Guid.NewGuid();
        public Guid Org=>native.Organization; public Guid Installation=>native.Legacy.Requester.Id;
        public Guid Package=>native.Legacy.Requester.PackageVersionId;
        public AgentOnboardingRecoveryService Service=>new(Db,Protection,Clock,Credentials);
        public DbContextOptions<CSweetDbContext> Options=> (DbContextOptions<CSweetDbContext>)native.Legacy.Db.GetService<IDbContextOptions>();
        public CSweetDbContext Context(IInterceptor? interceptor=null)=>new(new DbContextOptionsBuilder<CSweetDbContext>(Options)
            .UseInMemoryDatabase(Options.Extensions.OfType<Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal.InMemoryOptionsExtension>().Single().StoreName!,
                native.Legacy.Db.GetService<Microsoft.EntityFrameworkCore.InMemory.Storage.Internal.IInMemoryDatabaseRootCache>().SharedRoot)
            .UseModel(native.Legacy.Db.Model)
            .ConfigureWarnings(x=>x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).AddInterceptors(interceptor is null?[]:[interceptor]).Options);
        public Task<AgentWorkItem> Work()=>Db.AgentWorkItems.AsNoTracking().SingleAsync(x=>x.Id==WorkId);
        public Task<AgentOnboardingEventOutboxItem> Event()=>Db.AgentOnboardingEventOutbox.AsNoTracking().SingleAsync(x=>x.Id==EventId);
        public Task<OnboardingRecoveryReview> Preview()=>Service.PreviewAsync(Actor,Org,Installation,default);
        public Task<OnboardingRecoveryReceipt> Recover(OnboardingRecoveryReview review)=>Service.RecoverAsync(Actor,Org,Installation,review.ReviewToken,default);
        public async Task Change(Func<CSweetDbContext,Task> action) { await using var db=Context(); await action(db); await db.SaveChangesAsync(); }
        public McpAgentSession Session()
        {
            var runtime= new AgentRuntimeInstance {Id=Guid.NewGuid(),AgentInstallationId=Installation,TickId=Guid.NewGuid(),QueuedAt=Clock.GetUtcNow()};
            Db.AgentRuntimeInstances.Add(runtime);Db.SaveChanges();
            return new(){AgentInstallationId=Installation,OrganizationId=Org.ToString("D"),RuntimeInstanceId=runtime.Id,TickId=runtime.TickId,GrantRevision=1,PackageVersionId=Package};
        }
        public async Task<bool> Acknowledge()
        {
            await using var db=Context();
            var handler=new AgentOnboardingCapabilityHandler(db,Clock);
            var session=new AgentSession("session","example.agent",Installation.ToString("D"),Org.ToString("D"),Guid.NewGuid().ToString(),"tick",new(new HashSet<string>(),new HashSet<string>(),new HashSet<string>{AgentLifecycleCapabilities.CompleteOnboarding},1));
            var request=new RequestCapability {RequestId="ack",Capability=AgentLifecycleCapabilities.CompleteOnboarding,
                Payload=JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new CompleteAgentOnboardingRequest(EventId),Json))};
            await foreach(var result in handler.HandleAsync(session,request,default)) return result.Succeeded;
            return false;
        }
        public async Task Dispatch()
        {
            var services=new ServiceCollection();services.AddSingleton(Clock);services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(Protection);services.AddScoped(_=>Context());services.AddScoped<AgentWorkInbox>();services.AddScoped<AgentWorkRouter>();
            await using var provider=services.BuildServiceProvider();
            await new AgentOnboardingEventDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),Clock,
                Microsoft.Extensions.Options.Options.Create(new AgentOnboardingDeliveryOptions()),NullLogger<AgentOnboardingEventDispatcher>.Instance).DispatchPendingAsync(default);
        }
        public static async Task<Fixture> Create()
        {
            var f=new Fixture {native=await PluginAccountAuthorityTests.Fixture.Create()}; var db=f.Db;var now=f.Clock.GetUtcNow();
            var target=f.native.Legacy.Requester;target.PackageVersion!.Status=AgentPackageVersionStatus.Built;target.Scope=PluginInstallationScope.Organization;
            var manifest=JsonSerializer.Deserialize<PluginManifest>(target.PackageVersion.ManifestJson,Json)!;
            target.PackageVersion.ManifestJson=JsonSerializer.Serialize(manifest with {Requires=[..manifest.Requires,new(){Name=AgentLifecycleCapabilities.CompleteOnboarding}]},Json);
            target.Grant!.RequiredCapabilitiesJson=JsonSerializer.Serialize(new[]{ConnectorPlanServiceTests.Fixture.Capability,AgentLifecycleCapabilities.CompleteOnboarding});
            if(!await db.CoreOrganizations.AnyAsync(x=>x.Id==f.Org))db.CoreOrganizations.Add(new(){Id=f.Org,Name="Synthetic business",Status=OrganizationStatus.Active});
            var role=new IdentityRole<Guid>{Id=Guid.NewGuid(),Name=AuthenticationService.AdministratorRole};
            db.Roles.Add(role);db.Users.Add(new ApplicationUser{Id=f.Actor,UserName="synthetic-owner"});db.UserRoles.Add(new(){RoleId=role.Id,UserId=f.Actor});
            db.CoreOrganizationUsers.AddRange(new OrganizationUser{Id=f.Human,OrganizationId=f.Org,ApplicationUserId=f.Actor,EmployeeType=EmployeeType.Human,PermissionLevel=OrganizationPermissionLevel.Owner},
                new OrganizationUser{Id=f.Employee,OrganizationId=f.Org,AgentInstallationId=f.Installation,EmployeeType=EmployeeType.Agent,PermissionLevel=OrganizationPermissionLevel.Contributor});
            var conversation=new Conversation{Id=Guid.NewGuid(),OrganizationId=f.Org,AgentOrganizationUserId=f.Employee,InitiatedByOrganizationUserId=f.Human,Kind=ConversationKind.DirectHumanAgent,IsPrivate=true,IsDeletionProtected=true};db.CoreConversations.Add(conversation);
            var item=new AgentOnboardingEventOutboxItem{Id=f.EventId,OrganizationId=f.Org,AgentOrganizationUserId=f.Employee,HiringOrganizationUserId=f.Human,ConversationId=conversation.Id,
                Status=AgentOnboardingEventOutboxStatus.Failed,Attempts=1,OccurredAt=now.AddHours(-2),NextAttemptAt=now.AddMinutes(-1),LastError="safe prior failure"};db.AgentOnboardingEventOutbox.Add(item);
            var payload=JsonSerializer.SerializeToUtf8Bytes(new AgentOnboardedEvent(f.Org,f.Employee,f.Human,conversation.Id,item.OccurredAt),Json);
            var work=new AgentWorkItem{Id=f.WorkId,OrganizationId=f.Org.ToString("D"),AgentInstallationId=f.Installation,Kind=AgentWorkKind.Event,Name=AgentLifecycleEvents.Onboarded,
                Status=AgentWorkStatus.DeadLetter,IdempotencyKey=$"{AgentOnboardingEventDispatcher.CreateDeliveryKey(f.EventId,f.Package)}:{f.Installation:D}",SourceId=f.EventId.ToString("D"),SourceType="platform-event",
                CorrelationId="stable-synthetic-correlation",ProtectedPayload=f.Protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(payload),PayloadHash=Convert.ToHexString(SHA256.HashData(payload)),
                AttemptCount=1,MaximumAttempts=3,DeadlineAt=now.AddHours(-1),CreatedAt=now.AddHours(-2),LastError="agent-failure:v1;code=platform.capability.denied;retryable=false",ResultHash=new string('A',64),
                ProtectedResult=f.Protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(JsonSerializer.SerializeToUtf8Bytes(new AgentWorkCompletion(false,null,"safe", "platform.capability.denied",false)))};
            db.AgentWorkItems.Add(work);db.AgentWorkAttempts.Add(new(){Id=Guid.NewGuid(),AgentWorkItemId=f.WorkId,RuntimeInstanceId=Guid.NewGuid(),Attempt=1,ClaimedAt=now.AddHours(-2),LeaseExpiresAt=now.AddMinutes(-100),FinishedAt=now.AddMinutes(-110),CompletionHash="retained-prior-completion"});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();f.operational=f.Context();f.Credentials=ConnectorPlanServiceTests.Fixture.LocalCredentials(f.Db);return f;
        }
        public async ValueTask DisposeAsync(){if(operational is not null)await operational.DisposeAsync();await native.DisposeAsync();}
    }
}
