using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;
public sealed partial class WebPreviewDispatchTests
{
    [Fact]
    public async Task Lifecycle_events_track_semantic_changes_without_transport_noise_or_replay_duplicates()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var (job, initialize) = await f.StartToInitializeAsync();
        Assert.Single(await f.Db.AgentPlatformEventOutbox.ToListAsync());
        var ready = new ProductRuntimeResponse("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready));
        await f.CompleteAsync(initialize, ready);
        await f.CompleteAsync(initialize, ready);
        var events = await f.Db.AgentPlatformEventOutbox.OrderBy(x => x.OccurredAt).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, x => { Assert.Equal(f.Agent, x.TargetInstallationId); Assert.Equal(WebPreviewEvents.Changed, x.EventType); Assert.DoesNotContain("/open", x.DataJson); });
        var current = await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default);
        Assert.Contains(events, x => JsonSerializer.Deserialize<PreviewChangedEvent>(x.DataJson, PreviewJson.Options)!.Revision == current.Revision);
        var evidence = await f.PollAsync("evidence"); await f.CompleteAsync(evidence, new("Evidence", Evidence: new(job.Id, 0, [], false)));
        Assert.Equal(2, await f.Db.AgentPlatformEventOutbox.CountAsync());
        await f.Service.StopAsync(f.Org, f.Agent, job.Id, default);
        var stop = await f.PollAsync("stop"); await f.CompleteAsync(stop, new("Stopped"));
        Assert.Equal(4, await f.Db.AgentPlatformEventOutbox.CountAsync());
        Assert.Equal(PreviewPhase.Stopped, (await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default)).Phase);
    }

    [Fact]
    public async Task Wake_recovery_finds_terminal_previews_after_missed_events_and_provider_revocation()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var first = await f.Service.StartAsync(f.Org, f.Agent, f.Request("first"), default);
        var second = await f.Service.StartAsync(f.Org, f.Agent, f.Request("second"), default);
        var job = await f.Db.WebPreviewJobs.SingleAsync(x => x.Id == first.Id);
        job.Phase = "Expired"; job.FailureCode = "LeaseExpired"; job.TeardownConfirmedAt = f.Clock.Now;
        (await f.Db.AgentInstallations.SingleAsync(x => x.Id == f.Provider)).IsEnabled = false;
        f.Db.WebPreviewJobs.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, InstallationId = Guid.NewGuid(), WorkstreamId = f.Project, Phase = "Failed", CreatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();
        f.Db.AgentPlatformEventOutbox.RemoveRange(await f.Db.AgentPlatformEventOutbox.ToListAsync()); await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear(); // Simulate waking with no process-local state or retained notifications.
        var page = await f.Service.ListAsync(f.Org, f.Agent, new(f.Project, Limit: 1), default);
        Assert.Single(page.Items); Assert.NotNull(page.NextAfterId);
        var next = await f.Service.ListAsync(f.Org, f.Agent, new(f.Project, page.NextAfterId, 1), default);
        Assert.Single(next.Items); Assert.Null(next.NextAfterId);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), page.Items.Concat(next.Items).Select(x => x.Id).Order());
        Assert.Equal(PreviewPhase.Expired, (await f.Service.ReadAsync(f.Org, f.Agent, first.Id, default)).Phase);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ListAsync(f.Org, f.Agent, new(Guid.NewGuid()), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ListAsync(Guid.NewGuid(), f.Agent, new(f.Project), default));
    }

    [Fact]
    public void Recovery_keyset_query_translates_for_the_production_database()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql("Host=localhost;Database=translation_only").Options);
        var after = Guid.NewGuid();
        var sql = db.WebPreviewJobs.Where(x => x.Id.CompareTo(after) > 0).OrderBy(x => x.Id).Take(51).ToQueryString();
        Assert.Contains("ORDER BY", sql); Assert.Contains(">", sql);
    }

    [Fact]
    public async Task Preview_state_and_wake_event_rollback_together()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var job = new WebPreviewJobRecord { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), InstallationId = Guid.NewGuid(), Phase = "Ready", CreatedAt = DateTimeOffset.UtcNow };
        db.Add(job); await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            job.Phase = "Failed"; job.FailureCode = "RuntimeOperationFailed";
            await db.SaveChangesAsync(); Assert.Equal(2, await db.AgentPlatformEventOutbox.CountAsync());
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal("Ready", (await db.WebPreviewJobs.SingleAsync()).Phase);
        Assert.Single(await db.AgentPlatformEventOutbox.ToListAsync());
    }
}
