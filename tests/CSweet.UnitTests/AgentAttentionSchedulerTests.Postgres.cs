using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentAttentionSchedulerTests
{
    [AttentionPostgresFact]
    public async Task RelationalAttentionRollsBackInboxWithScheduleAndConcurrentRetryDeliversOnce()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "attention_test_" + Guid.NewGuid().ToString("N");
        settings.Pooling = false;
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var failure = new FailAttentionCommit();
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(o => o.UseNpgsql(settings.ConnectionString).AddInterceptors(failure));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        await using var provider = services.BuildServiceProvider();
        await using var owner = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql(settings.ConnectionString).Options);
        try
        {
            await owner.Database.EnsureCreatedAsync();
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/attention" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id,
                AgentId = "attention", Version = "1.0.0", ManifestJson = "{}" };
            var id = Guid.NewGuid();
            var correlation = Guid.NewGuid();
            var installation = new AgentInstallation
            {
                Id = id, InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id,
                BusinessId = Guid.NewGuid().ToString("D"),
                Grant = new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = id,
                    EventSubscriptionsJson = JsonSerializer.Serialize(new[] { AgentAttentionEvents.ReviewDue }) },
                Schedule = new AgentSchedule
                {
                    Id = Guid.NewGuid(), AgentInstallationId = id, IsEnabled = true,
                    ActivationMode = ActivationMode.OnDemand, TickFrequencySeconds = 300, MaxRuntimeSeconds = 300,
                    NextAttentionReviewAt = clock.Now, PendingAttentionReason = AgentAttentionReasons.StateChanged,
                    PendingAttentionCorrelationId = correlation, AttentionInvalidatedAt = clock.Now
                }
            };
            owner.AddRange(source, package, installation);
            await owner.SaveChangesAsync();
            var scheduler = new AgentAttentionScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
                clock, NullLogger<AgentAttentionScheduler>.Instance);

            // Fail after both SQL saves, before the surrounding transaction can commit.
            failure.Arm();
            await scheduler.DispatchDueReviewsAsync(CancellationToken.None);
            owner.ChangeTracker.Clear();
            Assert.True(failure.Fired);
            Assert.Empty(await owner.AgentWorkItems.ToListAsync());
            var unchanged = await owner.AgentSchedules.AsNoTracking().SingleAsync();
            Assert.Null(unchanged.LastAttentionReviewAt);
            Assert.Equal(correlation, unchanged.PendingAttentionCorrelationId);

            // Separate contexts/dispatchers contend on the same durable schedule lock.
            await Task.WhenAll(scheduler.DispatchDueReviewsAsync(CancellationToken.None),
                scheduler.DispatchDueReviewsAsync(CancellationToken.None));
            var work = await owner.AgentWorkItems.SingleAsync();
            Assert.Contains(correlation.ToString("N"), work.IdempotencyKey);
            var committed = await owner.AgentSchedules.AsNoTracking().SingleAsync();
            Assert.Equal(clock.Now, committed.LastAttentionReviewAt);
            Assert.Equal(clock.Now.AddMinutes(5), committed.NextAttentionReviewAt);
            Assert.Null(committed.PendingAttentionReason);
            Assert.Empty(await owner.AgentRuntimeInstances.ToListAsync());
        }
        finally { await owner.Database.EnsureDeletedAsync(); }
    }

    private sealed class FailAttentionCommit : SaveChangesInterceptor
    {
        private int _remaining;
        public bool Fired { get; private set; }
        public void Arm() => _remaining = 2;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                Fired = true;
                throw new InvalidOperationException("Injected attention commit failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AttentionPostgresFactAttribute : FactAttribute
    {
        public AttentionPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a local disposable PostgreSQL endpoint.";
        }
    }
}
