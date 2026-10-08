using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("content")]
    [InlineData("delete")]
    [InlineData("archive")]
    [InlineData("sender")]
    [InlineData("installation")]
    [InlineData("excluded")]
    [InlineData("role")]
    [InlineData("timestamp")]
    public async Task SourceReconciliationRetiresHistoricalMismatches(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={fixture.MessageId}");
        var original = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Name", "Alice", 1, 100, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow) { SourceEpisodeIds = [original.Id] });
        // EnsureCreated intentionally has no lifecycle triggers: simulate changes made by old binaries.
        switch (change)
        {
            case "content": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed historically")); break;
            case "delete": await db.CoreConversationMessages.ExecuteDeleteAsync(); break;
            case "archive": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "sender": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.SenderOrganizationUserId, fixture.EmployeeId)); break;
            case "installation": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentInstallationId, (Guid?)null)); break;
            case "excluded": await SeedExclusionAsync(fixture, fixture.MessageId); break;
            case "role": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Role, ConversationRole.Assistant)); break;
            case "timestamp": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(1))); break;
        }
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        await Reconciler(fixture, db).RunAsync();
        var checkpoint = await db.MemorySourceReconciliationCheckpoints.AsNoTracking().SingleAsync();
        Assert.NotNull(checkpoint.CompletedAt); Assert.Equal(1, checkpoint.ScannedEpisodes); Assert.Equal(1, checkpoint.SuppressedEpisodes);
        var retained = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.True(retained.IsSuppressed); Assert.True(retained.LegalHold);
        Assert.Equal(original.Content, retained.Content); Assert.Equal(original.SourceFingerprint, retained.SourceFingerprint);
        Assert.Equal("memory_source_reconciled", Assert.Single(await db.MemorySourceInvalidations.ToListAsync()).ReasonCode);
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(checkpoint));
        Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "Alice")));
        await Reconciler(fixture, db).RunAsync();
        Assert.Equal(JsonSerializer.Serialize(checkpoint), JsonSerializer.Serialize(await db.MemorySourceReconciliationCheckpoints.AsNoTracking().SingleAsync()));
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationPreservesMatchingLegacyEvidenceWithoutConfirmingOrReclassifyingIt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var original = await SeedLegacyEpisode(fixture);
        var before = await db.Database.SqlQuery<string>($"SELECT payload::text AS \"Value\" FROM csweet_memory_episodes WHERE id={original.Id}").SingleAsync();
        // A temporary availability change is not evidence that an immutable source was edited.
        await db.CoreOrganizationUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await db.AgentInstallations.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
        await Reconciler(fixture, db).RunAsync();
        var after = await db.Database.SqlQuery<string>($"SELECT payload::text AS \"Value\" FROM csweet_memory_episodes WHERE id={original.Id}").SingleAsync();
        Assert.Equal(before, after);
        Assert.Null(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).SourceFingerprint);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.Equal(0, (await db.MemorySourceReconciliationCheckpoints.SingleAsync()).SuppressedEpisodes);
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationSuppressesAmbiguousRecordsWithoutInventingSourceIdentity()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await SeedLegacyEpisode(fixture, change: e => e with { Metadata = new Dictionary<string, string> { ["conversationId"] = "invalid" } });
        await Reconciler(fixture, db).RunAsync();
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.Equal(1, (await db.MemorySourceReconciliationCheckpoints.SingleAsync()).SuppressedEpisodes);
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationPreservesMalformedPayloadWhileSuppressingItsKnownEpisode()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var original = await SeedLegacyEpisode(fixture);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{occurredAt}}',to_jsonb({"invalid-legacy-date"}::text)) WHERE id={original.Id}");
        await Reconciler(fixture, db).RunAsync();
        Assert.Equal("true", await db.Database.SqlQuery<string>($"SELECT payload->>'isSuppressed' AS \"Value\" FROM csweet_memory_episodes WHERE id={original.Id}").SingleAsync());
        Assert.Equal("invalid-legacy-date", await db.Database.SqlQuery<string>($"SELECT payload->>'occurredAt' AS \"Value\" FROM csweet_memory_episodes WHERE id={original.Id}").SingleAsync());
        Assert.Equal("preserve me", await db.Database.SqlQuery<string>($"SELECT payload->>'legacyExtension' AS \"Value\" FROM csweet_memory_episodes WHERE id={original.Id}").SingleAsync());
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.Equal(1, (await db.MemorySourceReconciliationCheckpoints.SingleAsync()).SuppressedEpisodes);
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationRecognizesConversationMetadataEvenIfSourceTypeIsMissing()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var original = await SeedLegacyEpisode(fixture);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{source}}','null'::jsonb) WHERE id={original.Id}");
        await Reconciler(fixture, db).RunAsync();
        Assert.Equal("true", await db.Database.SqlQuery<string>($"SELECT payload->>'isSuppressed' AS \"Value\" FROM csweet_memory_episodes WHERE id={original.Id}").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM csweet_memory_suppressions WHERE episode_id={original.Id}").SingleAsync());
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationRepairsAllKnownDuplicateCapturesButLeavesOtherApplicationsAlone()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var original = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        await fixture.Store.AppendEpisodeAsync(original with { Id = Guid.NewGuid(), IdempotencyKey = "duplicate" });
        var foreign = original.Partition with { ApplicationId = "another-application" };
        await fixture.Store.AppendEpisodeAsync(original with { Id = Guid.NewGuid(), Partition = foreign, IdempotencyKey = "foreign" });
        await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed before upgrade"));
        await Reconciler(fixture, db).RunAsync();
        Assert.All((await fixture.Store.ExportAsync(fixture.Partition)).Episodes, x => Assert.True(x.IsSuppressed));
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(foreign)).Episodes).IsSuppressed);
        Assert.Equal(2, (await db.MemorySourceReconciliationCheckpoints.SingleAsync()).SuppressedEpisodes);
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationResumesBoundedBatchesAndSerializesConcurrentMigrators()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var conversationId = (await db.CoreConversations.SingleAsync()).Id;
        var messages = Enumerable.Range(0, 4).Select(i => new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversationId,
            Role = ConversationRole.User, Content = $"Historical fact {i}", CreatedAt = DateTimeOffset.UtcNow }).ToArray();
        db.CoreConversationMessages.AddRange(messages); await db.SaveChangesAsync();
        foreach (var id in messages.Select(x => x.Id).Append(fixture.MessageId))
            await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(id);
        await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed before upgrade"));
        var first = await Reconciler(fixture, db).ProcessBatchAsync(1);
        Assert.Equal(1, first.ScannedEpisodes); Assert.Null(first.CompletedAt); Assert.NotNull(first.LastEpisodeId);
        await using var resumed = fixture.Context();
        var second = await Reconciler(fixture, resumed).ProcessBatchAsync(1);
        Assert.Equal(2, second.ScannedEpisodes); Assert.NotEqual(first.LastEpisodeId, second.LastEpisodeId);
        await using var concurrent = fixture.Context();
        await Task.WhenAll(Reconciler(fixture, resumed).RunAsync(), Reconciler(fixture, concurrent).RunAsync());
        var done = Assert.Single(await db.MemorySourceReconciliationCheckpoints.AsNoTracking().ToListAsync());
        Assert.NotNull(done.CompletedAt); Assert.Equal(5, done.ScannedEpisodes); Assert.Equal(1, done.SuppressedEpisodes);
        Assert.Equal(4, (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Count(x => !x.IsSuppressed));
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationCheckpointFailureRollsBackEveryRepairInTheBatch()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.CoreConversationMessages.ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_reconciliation_checkpoint() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected_checkpoint_failure'; END $$;
            CREATE TRIGGER fail_reconciliation_checkpoint AFTER UPDATE ON "MemorySourceReconciliationCheckpoints"
                FOR EACH ROW EXECUTE FUNCTION fail_reconciliation_checkpoint();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => Reconciler(fixture, db).ProcessBatchAsync());
        Assert.Empty(await db.MemorySourceReconciliationCheckpoints.ToListAsync());
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM csweet_memory_suppressions").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_reconciliation_checkpoint ON \"MemorySourceReconciliationCheckpoints\"; DROP FUNCTION fail_reconciliation_checkpoint();");
        await Reconciler(fixture, db).RunAsync();
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task SourceReconciliationMigrationPreservesSourcesAndGuardsConsumedProgress()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var migration = new MemorySourceReconciliation();
        await RunConversationMigrationAsync(db, migration.DownOperations);
        await RunConversationMigrationAsync(db, migration.UpOperations);
        await Reconciler(fixture, db).RunAsync();
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, migration.DownOperations));
        Assert.Equal(1, (await db.MemorySourceReconciliationCheckpoints.SingleAsync()).ScannedEpisodes);
    }

    private static MemorySourceReconciler Reconciler(DurabilityFixture fixture, CSweetDbContext db) =>
        new(db, fixture.Store, NullLogger<MemorySourceReconciler>.Instance);
}
