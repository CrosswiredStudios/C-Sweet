using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("archive")]
    [InlineData("merge")]
    [InlineData("privacy")]
    [InlineData("kind")]
    [InlineData("human")]
    [InlineData("agent")]
    public async Task ConversationLifecycleChangesSuppressExistingCaptures(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await InstallConversationLifecycleAsync(db);
        var service = fixture.Service(db, new UsageProviderFactory());
        await service.CaptureMessageAsync(fixture.MessageId);
        var original = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        await fixture.Store.AppendEpisodeAsync(original with { Id = Guid.NewGuid(), IdempotencyKey = "duplicate" });
        await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Name", "Alice", 1, 100, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow) { SourceEpisodeIds = [original.Id] });
        switch (change)
        {
            case "archive": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "merge": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.MergedIntoConversationId, x => x.Id)); break;
            case "privacy": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPrivate, true)); break;
            case "kind": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.Kind, ConversationKind.Team)); break;
            case "human":
                var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Human };
                db.CoreOrganizationUsers.Add(human); await db.SaveChangesAsync();
                await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.InitiatedByOrganizationUserId, human.Id)); break;
            case "agent": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentOrganizationUserId, (Guid?)null)); break;
        }
        Assert.Equal("memory_conversation_changed", Assert.Single(await db.MemorySourceInvalidations.AsNoTracking().ToListAsync()).ReasonCode);
        Assert.All((await fixture.Store.ExportAsync(fixture.Partition)).Episodes, x => Assert.True(x.IsSuppressed));
        Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "Alice")));
        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureMessageAsync(fixture.MessageId));
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleIgnoresPresentationChangesAndAllowsNewMessagesAfterReopening()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallConversationLifecycleAsync(db);
        var service = fixture.Service(db, new UsageProviderFactory());
        await service.CaptureMessageAsync(fixture.MessageId);
        await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Renamed")
            .SetProperty(x => x.Description, "Description").SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null));
        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureMessageAsync(fixture.MessageId));
        var next = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = (await db.CoreConversations.SingleAsync()).Id,
            SenderOrganizationUserId = fixture.HumanId, Role = ConversationRole.User, Content = "A new memory after reopening", CreatedAt = DateTimeOffset.UtcNow };
        db.CoreConversationMessages.Add(next); await db.SaveChangesAsync();
        await service.CaptureMessageAsync(next.Id);
        Assert.False((await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, next.Id))!.IsSuppressed);
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleFindsOrphanedDuplicateSourceIdentities()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var original = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        var duplicate = original with { Id = Guid.NewGuid(), IdempotencyKey = "orphan" };
        await fixture.Store.AppendEpisodeAsync(duplicate);
        // Simulate a source deleted before either lifecycle migration was installed.
        await db.CoreConversationMessages.ExecuteDeleteAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={original.Id}");
        await InstallConversationLifecycleAsync(db);
        await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        Assert.Equal(fixture.MessageId, Assert.Single(await db.MemorySourceInvalidations.ToListAsync()).SourceMessageId);
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleFailureRollsBackArchiveAndInvalidation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallConversationLifecycleAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_conversation_suppression() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected_conversation_failure'; END $$;
            CREATE TRIGGER reject_conversation_suppression BEFORE UPDATE ON csweet_memory_episodes
                FOR EACH ROW EXECUTE FUNCTION reject_conversation_suppression();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)));
        Assert.Null((await db.CoreConversations.AsNoTracking().SingleAsync()).ArchivedAt);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM csweet_memory_suppressions").SingleAsync());
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleArchiveWaitsForCaptureThenSuppressesIt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var capture = fixture.Context(); await capture.Database.OpenConnectionAsync();
        await InstallConversationLifecycleAsync(capture); await fixture.Store.InitializeAsync();
        await using var transaction = await capture.Database.BeginTransactionAsync();
        await fixture.Service(capture, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await using var archive = fixture.Context(); await archive.Database.OpenConnectionAsync();
        var pending = archive.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        try
        {
            await WaitForConversationLockAsync(capture, archive, pending);
            await transaction.CommitAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        }
        finally { await transaction.DisposeAsync(); await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleCaptureWaitsForArchiveAndThenRejectsSource()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var archive = fixture.Context(); await archive.Database.OpenConnectionAsync();
        await InstallConversationLifecycleAsync(archive); await fixture.Store.InitializeAsync();
        await using var transaction = await archive.Database.BeginTransactionAsync();
        await archive.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        await using var capture = fixture.Context(); await capture.Database.OpenConnectionAsync();
        var pending = fixture.Service(capture, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        try
        {
            await WaitForConversationLockAsync(archive, capture, pending);
            await transaction.CommitAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        }
        finally { await transaction.DisposeAsync(); try { await pending.WaitAsync(TimeSpan.FromSeconds(15)); } catch { } }
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationLifecycleOwnershipChangeRejectsConcurrentReviewWithoutDeadlock(bool captured)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var review = fixture.Context(); await review.Database.OpenConnectionAsync();
        await InstallConversationLifecycleAsync(review); await fixture.Store.InitializeAsync();
        if (captured) await fixture.Service(review, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await using var transaction = await review.Database.BeginTransactionAsync();
        await MemoryReviewWriteBarrier.AcquireAsync(review, default);
        await using var change = fixture.Context(); await change.Database.OpenConnectionAsync();
        var pending = change.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentOrganizationUserId, (Guid?)null));
        try
        {
            await WaitForConversationLockAsync(review, change, pending);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => MemoryManagerAuthorization.RequirePartitionAsync(review,
                fixture.OrganizationId, fixture.EmployeeId, fixture.HumanId, fixture.Partition, deadline.Token));
            await transaction.RollbackAsync(deadline.Token);
            await pending.WaitAsync(deadline.Token);
        }
        finally { await transaction.DisposeAsync(); await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
        await using var next = await review.Database.BeginTransactionAsync();
        await MemoryReviewWriteBarrier.AcquireAsync(review, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MemoryManagerAuthorization.RequirePartitionAsync(review,
            fixture.OrganizationId, fixture.EmployeeId, fixture.HumanId, fixture.Partition, default));
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleDeleteRetainsHeldMemoryWithBothCascadeTriggersInstalled()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await InstallConversationLifecycleAsync(db);
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={fixture.MessageId}");
        await db.CoreConversations.ExecuteDeleteAsync();
        Assert.Empty(await db.CoreConversationMessages.ToListAsync());
        Assert.Empty(await db.MemoryCaptureOutbox.ToListAsync());
        var retained = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.True(retained.IsSuppressed); Assert.True(retained.LegalHold);
        Assert.Equal("My name is Alice.", retained.Content);
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
        Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "Alice")));
    }

    [MemoryPostgresFact]
    public async Task ConversationLifecycleMigrationAllowsEmptyRollbackAndGuardsRetainedMarkers()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await InstallConversationLifecycleAsync(db);
        var migration = new ConversationMemoryLifecycle();
        await RunConversationMigrationAsync(db, migration.DownOperations);
        await RunConversationMigrationAsync(db, migration.UpOperations);
        await db.CoreConversations.ExecuteDeleteAsync();
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
        await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, migration.DownOperations));
        Assert.Single(await db.MemorySourceInvalidations.ToListAsync());
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("merge")]
    [InlineData("kind")]
    [InlineData("nonhuman")]
    [InlineData("sender")]
    [InlineData("role")]
    public async Task ConversationLifecycleCaptureRejectsIneligibleContextBeforeWriting(string changed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        await using var db = fixture.Context();
        var conversation = await db.CoreConversations.SingleAsync();
        var message = await db.CoreConversationMessages.SingleAsync();
        if (changed == "archive") conversation.ArchivedAt = DateTimeOffset.UtcNow;
        if (changed == "merge") conversation.MergedIntoConversationId = Guid.NewGuid();
        if (changed == "kind") conversation.Kind = ConversationKind.Team;
        if (changed == "nonhuman") (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).EmployeeType = EmployeeType.Agent;
        if (changed == "sender") message.SenderOrganizationUserId = fixture.EmployeeId;
        if (changed == "role") message.Role = (ConversationRole)99;
        await db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId));
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Empty(await db.AgentMemoryNamespaces.ToListAsync());
    }

    private static async Task WaitForConversationLockAsync(CSweetDbContext observer, CSweetDbContext blocked, Task pending)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pid = ((NpgsqlConnection)blocked.Database.GetDbConnection()).ProcessID;
        while (!await observer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid={pid} AND NOT granted) AS \"Value\"").SingleAsync(deadline.Token))
        {
            Assert.False(pending.IsCompleted, "The concurrent operation must wait for the lifecycle barrier.");
            await Task.Delay(25, deadline.Token);
        }
    }

    private static async Task InstallConversationLifecycleAsync(CSweetDbContext db)
    {
        await InstallSourceInvalidationAsync(db);
        await db.Database.OpenConnectionAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RunConversationMigrationAsync(db, new ConversationMemoryLifecycle().UpOperations);
        await transaction.CommitAsync();
    }

    private static async Task RunConversationMigrationAsync(CSweetDbContext db,
        IReadOnlyList<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> operations)
    {
        foreach (var sql in db.GetService<IMigrationsSqlGenerator>().Generate(operations))
        {
            // Execute generated migration SQL unchanged, including regex quantifier braces.
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql.CommandText;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class MemoryPostgresTheoryAttribute : TheoryAttribute
{
    public MemoryPostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")))
            Skip = "Set CSWEET_MEMORY_TEST_POSTGRES to an isolated PostgreSQL server with database creation permission.";
    }
}
