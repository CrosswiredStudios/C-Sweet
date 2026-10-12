using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class TrackedDelegationPostgresTests
{
    [DelegationPostgresFact]
    public async Task AtomicPersistenceSurvivesRestartAndResponseLossWithoutDuplicateTasksOrReports()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "delegation_test_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            var clock = new Clock(); await using var f = await TrackedDelegationTests.Fixture.Create(db, clock);
            var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
            var response = new DelegationResponseRequest(d.Id, d.Revision, "Accepted", "I will recheck staffing readiness.", "accept");
            d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, response);
            db.ChangeTracker.Clear();
            Assert.Equal(d.WorkItemId, (await f.Service.RespondAsync(f.Org, f.Gabriel.Id, response)).WorkItemId);
            Assert.Equal(2, await db.CoreWorkTasks.CountAsync());
            Assert.Single(await db.ChatTurns.ToListAsync());
            var pending = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
            var report = new PublishDelegationReportRequest(d.Id, pending.Update.Id, "Gabriel is rechecking staffing readiness.", "publish");
            var bound = await db.WorkTrackingSubscriptions.AsNoTracking().FirstAsync();
            db.WorkTrackingSubscriptions.Add(new() { Id = Guid.NewGuid(), OrganizationId = bound.OrganizationId,
                WorkItemId = bound.WorkItemId, FollowerId = bound.FollowerId, DelegationId = bound.DelegationId, CreatedAt = clock.Now });
            var outboxes = await db.ApplicationRealtimeOutbox.CountAsync();
            await Assert.ThrowsAsync<DbUpdateException>(() => f.Service.PublishAsync(f.Org, f.Naomi.Id, report));
            db.ChangeTracker.Clear();
            Assert.Null((await db.WorkTrackingUpdates.SingleAsync(x => x.Id == pending.Update.Id)).ReportedAt);
            Assert.Empty(await db.CoreConversationMessages.Where(x => x.IdempotencyKey == $"delegation-report:{pending.Update.Id:N}").ToListAsync());
            Assert.Equal(outboxes, await db.ApplicationRealtimeOutbox.CountAsync());
            await f.Service.PublishAsync(f.Org, f.Naomi.Id, report); db.ChangeTracker.Clear();
            await f.Service.PublishAsync(f.Org, f.Naomi.Id, report);
            Assert.Single(await db.CoreConversationMessages.Where(x => x.IdempotencyKey == $"delegation-report:{pending.Update.Id:N}").ToListAsync());
            Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
            d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
            for (var i = 0; i < 30; i++)
            {
                clock.Now += TimeSpan.FromMilliseconds(1);
                d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Progress", $"Recovery check {i}.", $"progress:{i}"));
            }
            clock.Now += TimeSpan.FromMinutes(5);
            var progress = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 1));
            Assert.Equal("Recovery check 29.", progress.Update.Summary);
            d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
            await f.Service.CancelAsync(f.Org, f.Naomi.Id, new(d.Id, d.Revision, "cancel-after-restart"));
            db.ChangeTracker.Clear();
            d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
            Assert.True(d.TrackingCancelled); Assert.Equal("Active", d.ExecutionStatus);
            d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Progress", "Execution continues without updates.", "execution-after-cancel"));
            Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
            Assert.All(await db.WorkTrackingSubscriptions.Where(x => x.DelegationId == d.Id).ToListAsync(), x => Assert.NotNull(x.UnfollowedAt));
            Assert.NotEqual(WorkTaskStatus.Cancelled, (await db.CoreWorkTasks.SingleAsync(x => x.Id == d.WorkItemId)).Status);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class DelegationPostgresFactAttribute : FactAttribute
    {
        public DelegationPostgresFactAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES"))) Skip = "Set CSWEET_DELEGATION_TEST_POSTGRES to a disposable loopback server."; }
    }
}
