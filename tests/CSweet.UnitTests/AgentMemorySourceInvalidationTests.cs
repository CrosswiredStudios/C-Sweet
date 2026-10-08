using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task SourceInvalidationDuringProviderCallCannotAcceptExtraction()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallSourceInvalidationAsync(db);
        var factory = new ScriptedProviderFactory(async (_, _) =>
        {
            await using var editor = fixture.Context();
            await editor.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Edited during inference"));
        });
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
        Assert.Equal("memory_source_changed", job.LastError);
        Assert.Null(job.AcceptedExtractionJson);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationEditSuppressesDuplicateCapturesAndDerivativesPreservingHolds()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await InstallSourceInvalidationAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var original = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        await fixture.Store.AppendEpisodeAsync(original with { Id = Guid.NewGuid(), IdempotencyKey = "duplicate" });
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={original.Id}");
        await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Name", "Alice", 1, 100, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow) { SourceEpisodeIds = [original.Id] });
        Assert.NotEmpty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "Alice")));

        // Bulk SQL bypasses SaveChanges hooks, but not the lifecycle trigger.
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "My name is Bob."));
        var marker = Assert.Single(await db.MemorySourceInvalidations.AsNoTracking().ToListAsync());
        Assert.Equal("memory_source_edited", marker.ReasonCode);
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(marker));
        var captured = (await fixture.Store.ExportAsync(fixture.Partition)).Episodes;
        Assert.Equal(2, captured.Count); Assert.All(captured, x => Assert.True(x.IsSuppressed));
        Assert.True(captured.Single(x => x.Id == original.Id).LegalHold);
        Assert.Equal(original.Checksum, captured.Single(x => x.Id == original.Id).Checksum);
        Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "Alice")));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Store.AppendEpisodeAsync(original with { Id = Guid.NewGuid(), IdempotencyKey = "late" }));
        await Assert.ThrowsAnyAsync<Exception>(() => db.MemorySourceInvalidations.ExecuteDeleteAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => db.MemorySourceInvalidations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReasonCode, "reset")));
        Assert.Single(await db.MemorySourceInvalidations.AsNoTracking().ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationAllowsInitialTurnBindingButRejectsReassociation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallSourceInvalidationAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.ChatTurnId, Guid.NewGuid()));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.ChatTurnId, Guid.NewGuid()));
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationIgnoresNonEvidenceUpdatesAndNoOpEdits()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallSourceInvalidationAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, x => x.Content)
            .SetProperty(x => x.CorrelationId, Guid.NewGuid()));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Role, ConversationRole.Assistant));
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationCascadeDeletionRetainsEvidenceAndSurvivesRestoredMessage()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await InstallSourceInvalidationAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE saved_conversation AS SELECT * FROM "CoreConversations";
            CREATE TEMP TABLE saved_message AS SELECT * FROM "CoreConversationMessages";
            """);
        await db.CoreConversations.ExecuteDeleteAsync();
        Assert.Empty(await db.CoreConversationMessages.ToListAsync());
        Assert.Empty(await db.MemoryCaptureOutbox.ToListAsync());
        Assert.Equal("memory_source_deleted", Assert.Single(await db.MemorySourceInvalidations.ToListAsync()).ReasonCode);
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "CoreConversations" SELECT * FROM saved_conversation;
            INSERT INTO "CoreConversationMessages" SELECT * FROM saved_message;
            """);
        db.ChangeTracker.Clear();
        var service = fixture.Service(db, new UsageProviderFactory());
        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureMessageAsync(fixture.MessageId));
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Empty(await db.MemoryCaptureOutbox.ToListAsync());
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationBeforeLibraryInitializationPreventsCaptureAndUnsafeRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var (actor, jobId) = await SeedRecoveryAsync(fixture);
        await InstallSourceInvalidationAsync(db);
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Edited before capture"));
        var factory = new UsageProviderFactory();
        var service = fixture.Service(db, factory);
        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureMessageAsync(fixture.MessageId));
        var recovery = new AgentMemoryRecoveryService(db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, actor, new(Guid.NewGuid(), 0)));
        Assert.Equal("memory_source_changed", Assert.Single((await recovery.ListFailuresAsync(fixture.OrganizationId,
            fixture.EmployeeId, actor)).Items).FailureCode);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Null(factory.SelectedProviderId);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationFailureRollsBackMessageMarkerAndSuppression()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallSourceInvalidationAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_source_change() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected_suppression_failure'; END $$;
            CREATE TRIGGER reject_source_change BEFORE UPDATE ON csweet_memory_episodes
                FOR EACH ROW EXECUTE FUNCTION reject_source_change();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed")));
        Assert.Equal("My name is Alice.", (await db.CoreConversationMessages.AsNoTracking().SingleAsync()).Content);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM csweet_memory_suppressions").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_source_change ON csweet_memory_episodes; DROP FUNCTION reject_source_change();");
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed"));
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationWaitsForAtomicCaptureAndSuppressesItsCommittedEpisode()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var capture = fixture.Context(); await capture.Database.OpenConnectionAsync();
        await InstallSourceInvalidationAsync(capture); await fixture.Store.InitializeAsync();
        await using var edit = fixture.Context(); await edit.Database.OpenConnectionAsync();
        await using var transaction = await capture.Database.BeginTransactionAsync();
        await fixture.Service(capture, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var pending = edit.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed concurrently"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var pid = ((NpgsqlConnection)edit.Database.GetDbConnection()).ProcessID;
            while (!await capture.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid={pid} AND NOT granted) AS \"Value\"").SingleAsync(deadline.Token))
            {
                Assert.False(pending.IsCompleted, "An edit must wait for the original-message capture lock.");
                await Task.Delay(25, deadline.Token);
            }
            Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
            await transaction.CommitAsync(deadline.Token);
            await pending.WaitAsync(deadline.Token);
            Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        }
        finally
        {
            await transaction.DisposeAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationCaptureRollbackDoesNotLeaveOrphanedMemory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await fixture.Store.InitializeAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Empty(await db.AgentMemoryNamespaces.ToListAsync());
        Assert.Null((await db.MemoryCaptureOutbox.SingleAsync()).EpisodeCapturedAt);
    }

    [MemoryPostgresFact]
    public async Task SourceInvalidationMigrationAllowsEmptyRollbackButPreservesExistingMarkers()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallSourceInvalidationAsync(db);
        var migration = new ConversationMemoryInvalidation();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await db.CoreConversationMessages.ExecuteDeleteAsync();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(generator.Generate(migration.DownOperations)[0].CommandText));
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
        Assert.Empty(db.Model.FindEntityType(typeof(MemorySourceInvalidation))!.GetForeignKeys());
    }

    private static async Task InstallSourceInvalidationAsync(CSweetDbContext db)
    {
        // EnsureCreated builds tables, not custom migration triggers. Exercise the real upgrade.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"MemorySourceInvalidations\"");
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(new ConversationMemoryInvalidation().UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await transaction.CommitAsync();
    }
}
