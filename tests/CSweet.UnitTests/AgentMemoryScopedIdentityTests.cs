using CSweet.AgentHost.Broker;
using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static (string Save, string Delete, string Restore) ScopedIdentitySql(string table) => table switch
    {
        "CoreConversations" => ("CREATE TEMP TABLE saved_scope AS SELECT * FROM \"CoreConversations\" WHERE \"Id\"={0}",
            "DELETE FROM \"CoreConversations\" WHERE \"Id\"={0}", "INSERT INTO \"CoreConversations\" SELECT * FROM saved_scope"),
        "CoreWorkTasks" => ("CREATE TEMP TABLE saved_scope AS SELECT * FROM \"CoreWorkTasks\" WHERE \"Id\"={0}",
            "DELETE FROM \"CoreWorkTasks\" WHERE \"Id\"={0}", "INSERT INTO \"CoreWorkTasks\" SELECT * FROM saved_scope"),
        "WorkBoards" => ("CREATE TEMP TABLE saved_scope AS SELECT * FROM \"WorkBoards\" WHERE \"Id\"={0}",
            "DELETE FROM \"WorkBoards\" WHERE \"Id\"={0}", "INSERT INTO \"WorkBoards\" SELECT * FROM saved_scope"),
        _ => throw new ArgumentException("Unsupported identity fixture.")
    };

    [MemoryPostgresTheory]
    [InlineData("Conversation")]
    [InlineData("Case")]
    [InlineData("Board")]
    public async Task ScopedAudienceDeletedIdentityCannotInheritOldMemoryOrDispatchReceipts(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind == "Conversation" ? kind : "Case");
        var turn = await SeedRecallTurnAsync(fixture);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var currentWork = await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
        currentWork.SourceType = "chat-turn"; currentWork.SourceId = turn.Id.ToString("D");
        await db.SaveChangesAsync();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, scoped.Episode.Id, scoped.User);
        var request = ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice"));
        var read = await ReadHandler(fixture, db).HandleAsync(session, request, default);
        Assert.True(read.Succeeded, read.Error);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);

        var (table, id) = kind switch
        {
            "Conversation" => ("CoreConversations", scoped.Conversation),
            "Case" => ("CoreWorkTasks", scoped.Item!.Value),
            _ => ("WorkBoards", await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).Select(x => x.BoardId!.Value).SingleAsync())
        };
        var sql = ScopedIdentitySql(table);
        await db.Database.ExecuteSqlRawAsync(sql.Save, id);
        if (kind == "Board") await db.CoreWorkTasks.Where(x => x.Id == scoped.Item)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.BoardId, (Guid?)null));
        if (kind == "Conversation") await db.ChatTurns.Where(x => x.ConversationId == id).ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync(sql.Delete, id);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            sql.Restore));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Contains("cannot be reused", failure.MessageText);
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(scoped.Episode.Partition, scoped.Episode.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.SuppressSourceAsync(fixture.OrganizationId,
            fixture.EmployeeId, scoped.Episode.Id, scoped.User, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() =>
            new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        var denied = await ReadHandler(fixture, db).HandleAsync(session, request, default);
        Assert.False(denied.Succeeded);
        Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());

        // The deleted resource can be replaced with a fresh identity without adopting its memory.
        var replacement = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("UPDATE saved_scope SET \"Id\"={0}", replacement);
        await db.Database.ExecuteSqlRawAsync(sql.Restore);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM "MemoryScopeIdentityHistory"
            WHERE "ResourceId"={id} AND "DeletedAt" IS NOT NULL
            """).SingleAsync());
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceIdentityHistoryCannotBeRewoundOrTruncated()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        await using var db = fixture.Context();
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteDeleteAsync();
        foreach (var command in new[]
        {
            "UPDATE \"MemoryScopeIdentityHistory\" SET \"DeletedAt\"=NULL",
            "DELETE FROM \"MemoryScopeIdentityHistory\"",
            "TRUNCATE \"MemoryScopeIdentityHistory\"",
            "TRUNCATE \"CoreConversations\" CASCADE",
            "TRUNCATE \"CoreWorkTasks\" CASCADE",
            "TRUNCATE \"WorkBoards\" CASCADE",
            "UPDATE \"CoreConversations\" SET \"Id\"=gen_random_uuid()",
            "UPDATE \"WorkBoards\" SET \"Id\"=gen_random_uuid()"
        })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(command));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        }
        var migration = new ScopedMemoryIdentityHistory();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var downgrade = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations))
                await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("allow deleted audience identities", downgrade.MessageText);
        Assert.Single(await db.CoreConversations.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.WorkBoards.AsNoTracking().ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceIdentityMigrationFencesHistoricalMemoryWhoseParentWasAlreadyDeleted(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var migration = new ScopedMemoryIdentityHistory();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var table = kind == "Case" ? "CoreWorkTasks" : "CoreConversations";
        var id = kind == "Case" ? scoped.Item!.Value : scoped.Conversation;
        var sql = ScopedIdentitySql(table);
        await db.Database.ExecuteSqlRawAsync(sql.Save, id);
        await db.Database.ExecuteSqlRawAsync(sql.Delete, id);
        await using (var upgrade = await db.Database.BeginTransactionAsync())
        {
            foreach (var command in generator.Generate(migration.UpOperations))
                await db.Database.ExecuteSqlRawAsync(command.CommandText.Replace("{", "{{").Replace("}", "}}"));
            await upgrade.CommitAsync();
        }
        Assert.False(db.Database.HasPendingModelChanges());
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            sql.Restore));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Contains("cannot be reused", failure.MessageText);
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(scoped.Episode.Partition, scoped.Episode.Id));
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceConcurrentDeleteAndRecreationAreSerializedByIdentityFence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        await using var writer = fixture.Context();
        var original = await writer.Database.SqlQuery<string>($"""
            SELECT to_jsonb(t)::text AS "Value" FROM "CoreWorkTasks" t WHERE "Id"={scoped.Item!.Value}
            """).SingleAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteDeleteAsync();
        await using var connection = fixture.IndependentConnection();
        await connection.OpenAsync();
        await using var restore = new NpgsqlCommand("""
            INSERT INTO "CoreWorkTasks" SELECT * FROM jsonb_populate_record(NULL::"CoreWorkTasks",@row)
            """, connection) { CommandTimeout = 10 };
        restore.Parameters.AddWithValue("row", NpgsqlDbType.Jsonb, original);
        var pending = restore.ExecuteNonQueryAsync();
        var blocked = false;
        for (var attempt = 0; attempt < 100 && !pending.IsCompleted; attempt++)
        {
            blocked = await writer.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({connection.ProcessID}))>0 AS \"Value\"").SingleAsync();
            if (blocked) break;
            await Task.Delay(20);
        }
        // Always release the writer before asserting or observing the pending result.
        await transaction.CommitAsync();
        var failure = await Assert.ThrowsAsync<PostgresException>(async () => await pending);
        Assert.True(blocked, "The restore must wait for the deletion transaction's identity fence.");
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.False(await writer.CoreWorkTasks.AsNoTracking().AnyAsync(x => x.Id == scoped.Item));
    }
}
