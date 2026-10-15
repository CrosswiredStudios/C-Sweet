using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentPlatformEventDispatcherTests
{
    [Fact]
    public async Task HostCancellationLeavesTheUndeliveredEventPendingWithoutConsumingAnAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var database = Guid.NewGuid().ToString("N");
        await using var provider = RecoveryServices(o => o.UseInMemoryDatabase(database)
            .AddInterceptors(new CancelInboxSave(cancellation))).BuildServiceProvider();
        var org = Guid.NewGuid();
        var target = Installation(org, [WorkstreamEventNames.ArtifactRevisionDecidedV1]);
        var item = RecoveryEvent(org, target.Id, WorkstreamEventNames.ArtifactRevisionDecidedV1, "cancelled-dispatch");
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.AddRange(target, item); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecoveryDispatcher(provider).DispatchPendingAsync(cancellation.Token));
        await using var verify = provider.CreateAsyncScope();
        var final = verify.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Empty(await final.AgentWorkItems.ToListAsync());
        var pending = await final.AgentPlatformEventOutbox.SingleAsync();
        Assert.Equal(AgentPlatformEventOutboxStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
        Assert.Null(pending.LastError);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(143)]
    [InlineData(163)]
    [InlineData(164)]
    [InlineData(200)]
    public async Task EventKeysFitInboxLimitsAndReplayWithoutCollapsingRecipientsOrSharedPrefixes(int keyLength)
    {
        var database = Guid.NewGuid().ToString("N");
        await using var provider = RecoveryServices(o => o.UseInMemoryDatabase(database)).BuildServiceProvider();
        var org = Guid.NewGuid();
        var first = Installation(org, [WorkstreamEventNames.ArtifactRevisionDecidedV1]);
        var second = Installation(org, [WorkstreamEventNames.ArtifactRevisionDecidedV1]);
        var key = new string('a', keyLength - 1) + "x";
        var one = RecoveryEvent(org, first.Id, WorkstreamEventNames.ArtifactRevisionDecidedV1, key);
        one.TargetInstallationId = null;
        var two = RecoveryEvent(org, first.Id, one.EventType, key[..^1] + "y");
        two.TargetInstallationId = null;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.AddRange(first, second, one, two);
            await db.SaveChangesAsync();
            // Previously valid keys must continue to discover work delivered by an older host.
            if (keyLength <= 128)
                await scope.ServiceProvider.GetRequiredService<AgentWorkInbox>().EnqueueAsync(org.ToString("D"), first.Id,
                    AgentWorkKind.Event, one.EventType, JsonSerializer.SerializeToElement(new { }), $"{key}:{first.Id:D}",
                    DateTimeOffset.UtcNow.AddHours(1), correlationId: key, sourceType: "platform-event", sourceId: one.Id.ToString("D"));
        }
        var dispatcher = RecoveryDispatcher(provider);
        await dispatcher.DispatchPendingAsync(default);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var work = await db.AgentWorkItems.ToListAsync();
            Assert.Equal(4, work.Count);
            Assert.Equal(4, work.Select(x => x.IdempotencyKey).Distinct().Count());
            Assert.All(work, x => { Assert.True(x.CorrelationId.Length <= 128); Assert.True(x.IdempotencyKey.Length <= 200); });
            Assert.All(work.Where(x => x.SourceId == one.Id.ToString("D")), x =>
                Assert.Equal(keyLength <= 128 ? key : $"platform-event:{one.Id:D}", x.CorrelationId));
            if (keyLength <= 163)
                Assert.Contains(work, x => x.IdempotencyKey == $"{key}:{first.Id:D}");
            // Acknowledge-loss recovery must reuse both recipients' existing work.
            foreach (var item in await db.AgentPlatformEventOutbox.ToListAsync())
            { item.Status = AgentPlatformEventOutboxStatus.Pending; item.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); }
            await db.SaveChangesAsync();
        }
        await dispatcher.DispatchPendingAsync(default);
        await using var verify = provider.CreateAsyncScope();
        var final = verify.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Equal(4, await final.AgentWorkItems.CountAsync());
        Assert.All(await final.AgentPlatformEventOutbox.ToListAsync(), x => Assert.Equal(AgentPlatformEventOutboxStatus.Published, x.Status));
    }

    [EventDeliveryPostgresFact]
    public async Task DatabaseInsertFailureCannotBlockLongKeysOrPitchApprovalAndAcknowledgementReplayIsIdempotent()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_EVENT_DELIVERY_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "event_delivery_test_" + Guid.NewGuid().ToString("N");
        settings.Pooling = false;
        await using var provider = RecoveryServices(o => o.UseNpgsql(settings.ConnectionString)).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        try
        {
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var org = new Organization { Id = Guid.NewGuid(), Name = "Event recovery regression" };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/event-recovery" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "event-recovery", Version = "1.0.0", ManifestJson = "{}" };
            var target = Installation(org.Id, ["test.insert-failure", WorkstreamEventNames.ArtifactRevisionDecidedV1]);
            target.PackageVersionId = package.Id;
            db.AddRange(org, source, package, target);
            await db.SaveChangesAsync();
            var poison = RecoveryEvent(org.Id, target.Id, "test.insert-failure", "failed-inbox-insert");
            poison.OccurredAt = DateTimeOffset.UtcNow.AddHours(-1);
            poison.Attempts = 11;
            var longCorrelation = RecoveryEvent(org.Id, target.Id, WorkstreamEventNames.ArtifactRevisionDecidedV1, new string('a', 143));
            var longDelivery = RecoveryEvent(org.Id, target.Id, longCorrelation.EventType, new string('a', 199) + "x");
            var sharedPrefix = RecoveryEvent(org.Id, target.Id, longCorrelation.EventType, new string('a', 199) + "y");
            var approval = RecoveryEvent(org.Id, target.Id, longCorrelation.EventType, "accepted-pitch-revision");
            db.AddRange(poison, longCorrelation, longDelivery, sharedPrefix, approval);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_event_inbox_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Name" = 'test.insert-failure' THEN RAISE EXCEPTION 'disposable event inbox failure'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER fail_event_inbox_insert BEFORE INSERT ON "AgentWorkItems"
                FOR EACH ROW EXECUTE FUNCTION fail_event_inbox_insert();
                """);
            var dispatcher = RecoveryDispatcher(provider);
            await dispatcher.DispatchPendingAsync(default);
            db.ChangeTracker.Clear();
            var failed = await db.AgentPlatformEventOutbox.SingleAsync(x => x.Id == poison.Id);
            Assert.Equal(AgentPlatformEventOutboxStatus.Failed, failed.Status);
            Assert.Equal(12, failed.Attempts);
            Assert.NotNull(failed.LastError);
            Assert.True(failed.NextAttemptAt > poison.NextAttemptAt);
            Assert.Equal(4, await db.AgentWorkItems.CountAsync());
            Assert.All(await db.AgentPlatformEventOutbox.Where(x => x.Id != poison.Id).ToListAsync(), x =>
                Assert.Equal(AgentPlatformEventOutboxStatus.Published, x.Status));
            Assert.Contains(await db.AgentWorkItems.ToListAsync(), x => x.SourceId == approval.Id.ToString("D"));

            // A real database failure AFTER inbox persistence must retain a retry receipt,
            // and replay must find that inbox item rather than emit another generation.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_event_acknowledgement() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."IdempotencyKey" = 'lost-acknowledgement' AND NEW."Status" = 'Published'
                    THEN RAISE EXCEPTION 'disposable acknowledgement failure'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER fail_event_acknowledgement BEFORE UPDATE ON "AgentPlatformEventOutbox"
                FOR EACH ROW EXECUTE FUNCTION fail_event_acknowledgement();
                """);
            var lost = RecoveryEvent(org.Id, target.Id, approval.EventType, "lost-acknowledgement");
            db.Add(lost); await db.SaveChangesAsync();
            await dispatcher.DispatchPendingAsync(default);
            db.ChangeTracker.Clear();
            var retry = await db.AgentPlatformEventOutbox.SingleAsync(x => x.Id == lost.Id);
            Assert.Equal(AgentPlatformEventOutboxStatus.Pending, retry.Status);
            Assert.Equal(1, retry.Attempts);
            Assert.NotNull(retry.LastError);
            Assert.Equal(5, await db.AgentWorkItems.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_event_acknowledgement ON \"AgentPlatformEventOutbox\"; DROP FUNCTION fail_event_acknowledgement();");
            retry.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
            await dispatcher.DispatchPendingAsync(default);
            await dispatcher.DispatchPendingAsync(default);
            db.ChangeTracker.Clear();
            Assert.Equal(5, await db.AgentWorkItems.CountAsync());
            retry = await db.AgentPlatformEventOutbox.SingleAsync(x => x.Id == lost.Id);
            Assert.Equal(AgentPlatformEventOutboxStatus.Published, retry.Status);
            Assert.Equal(2, retry.Attempts);
            Assert.Null(retry.LastError);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static ServiceCollection RecoveryServices(Action<DbContextOptionsBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(configure);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentWorkInbox>(); services.AddScoped<AgentWorkRouter>();
        services.AddSingleton<IAgentRuntimeManager>(new RecordingRuntimeManager());
        return services;
    }

    private static AgentPlatformEventDispatcher RecoveryDispatcher(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AgentPlatformEventDispatcher>.Instance);

    private static AgentPlatformEventOutboxItem RecoveryEvent(Guid org, Guid installation, string type, string key) =>
        new() { Id = Guid.NewGuid(), OrganizationId = org, TargetInstallationId = installation, EventType = type,
            DataJson = "{}", IdempotencyKey = key, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1), NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

    private sealed class EventDeliveryPostgresFactAttribute : FactAttribute
    {
        public EventDeliveryPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_EVENT_DELIVERY_TEST_DATABASE")))
                Skip = "Set CSWEET_EVENT_DELIVERY_TEST_DATABASE to a disposable loopback PostgreSQL server.";
        }
    }

    private sealed class CancelInboxSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AgentWorkItem>().Any(x => x.State == EntityState.Added))
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return ValueTask.FromResult(result);
        }
    }
}
