using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class DecisionContentPostgresTests
{
    [DecisionPostgresFact]
    public async Task MigrationPreservesExistingDecisionsAndLongUnicodeContentRoundTripsWithoutColumnCaps()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "decision_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options);
        var adminSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {Quote(settings.Database)}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await db.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var migration = new UnboundDecisionContent();
            var reportMigration = new UnboundPersonalWorkContent();
            var operations = migration.UpOperations.Concat(reportMigration.UpOperations).ToArray();
            var changes = operations.Cast<AlterColumnOperation>().ToArray();
            Assert.Equal(13, changes.Length);
            // Exercise the real ALTER migration against the old PostgreSQL column types.
            // Other application tables are irrelevant to this storage regression.
            foreach (var group in changes.GroupBy(x => x.Table))
            {
                var columns = string.Join(", ", group.Select(x => $"{Quote(x.Name)} {x.OldColumn.ColumnType}"));
                await using var create = new NpgsqlCommand($"CREATE TABLE {Quote(group.Key)} (\"Id\" integer PRIMARY KEY, {columns})", connection);
                await create.ExecuteNonQueryAsync();
                var names = string.Join(", ", group.Select(x => Quote(x.Name)));
                var values = string.Join(", ", group.Select(_ => "'Existing decision'"));
                await using var seed = new NpgsqlCommand($"INSERT INTO {Quote(group.Key)} (\"Id\", {names}) VALUES (1, {values})", connection);
                await seed.ExecuteNonQueryAsync();
            }
            var sql = db.GetService<IMigrationsSqlGenerator>().Generate(operations, db.Model);
            foreach (var statement in sql)
            {
                await using var apply = new NpgsqlCommand(statement.CommandText, connection); await apply.ExecuteNonQueryAsync();
            }
            var longText = string.Concat(Enumerable.Repeat(DecisionContentTests.IncidentText() + "\n🎮 日本語\n", 40));
            foreach (var column in changes)
            {
                await using var read = new NpgsqlCommand($"SELECT {Quote(column.Name)} FROM {Quote(column.Table)} WHERE \"Id\" = 1", connection);
                Assert.Equal("Existing decision", await read.ExecuteScalarAsync());
                await using var write = new NpgsqlCommand($"UPDATE {Quote(column.Table)} SET {Quote(column.Name)} = @content WHERE \"Id\" = 1", connection);
                write.Parameters.AddWithValue("content", longText); await write.ExecuteNonQueryAsync();
                Assert.Equal(longText, await read.ExecuteScalarAsync());
                await using var metadata = new NpgsqlCommand("SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND column_name = @column", connection);
                metadata.Parameters.AddWithValue("table", column.Table); metadata.Parameters.AddWithValue("column", column.Name);
                Assert.Equal("text", await metadata.ExecuteScalarAsync());
            }
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
            Assert.StartsWith("decision_test_", settings.Database);
            await using var drop = new NpgsqlCommand($"DROP DATABASE {Quote(settings.Database)} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private sealed class DecisionPostgresFactAttribute : FactAttribute
    {
        public DecisionPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE for isolated PostgreSQL tests.";
        }
    }
}
