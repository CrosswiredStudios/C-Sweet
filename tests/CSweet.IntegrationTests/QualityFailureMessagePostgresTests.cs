using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.IntegrationTests;

public sealed class QualityFailureMessagePostgresTests
{
    [BenchmarkPostgresFact]
    public async Task MigrationRemovesLegacyLimitAndRoundTripsLongFailureMessages()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_EFFICIENCY_TEST_POSTGRES"));
        Assert.StartsWith("efficiency_validation", connection.Database);
        connection.Database = "efficiency_validation_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261020042226_RoleScopedManualHiring");
            await db.Database.MigrateAsync();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT data_type FROM information_schema.columns WHERE table_name='SourceControlValidations' AND column_name='FailureMessage'";
            Assert.Equal("text", await command.ExecuteScalarAsync());
            command.CommandText = "CREATE TEMP TABLE quality_result_roundtrip (LIKE \"SourceControlValidations\");";
            await command.ExecuteNonQueryAsync();
            foreach (var length in new[] { 3415, 16384 })
            {
                var summary = new string('q', length);
                command.CommandText = "INSERT INTO quality_result_roundtrip (\"Id\", \"OrganizationId\", \"PublicationId\", \"ValidatorAgentInstallationId\", \"CommitSha\", \"Status\", \"ResultsJson\", \"CreatedAt\", \"UpdatedAt\", \"FailureMessage\") VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), repeat('a',40), 'Failed', '{}', now(), now(), @summary) RETURNING \"FailureMessage\";";
                command.Parameters.Clear();
                command.Parameters.Add(new NpgsqlParameter("summary", summary));
                Assert.Equal(summary, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
