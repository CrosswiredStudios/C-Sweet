using CSweet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class LlmProviderConcurrencyMigrationTests
{
    [Fact]
    public async Task Migration_defaults_existing_and_future_profiles_to_four_and_matches_model()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_script_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261012220542_LlmProviderConcurrency"], db.Database.ProviderName!);
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, migration.TargetModel);

        // Execute the additive column SQL against a small table to verify its data default,
        // without running unrelated historical migrations or touching the live database.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE \"LlmProviderProfiles\" (\"Id\" TEXT PRIMARY KEY); INSERT INTO \"LlmProviderProfiles\" VALUES ('existing');";
        await command.ExecuteNonQueryAsync();
        foreach (var sql in commands)
        {
            command.CommandText = sql.CommandText;
            await command.ExecuteNonQueryAsync();
        }
        command.CommandText = "INSERT INTO \"LlmProviderProfiles\" (\"Id\") VALUES ('new'); SELECT COUNT(*) FROM \"LlmProviderProfiles\" WHERE \"MaximumConcurrentRequests\" = 4;";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }
}
