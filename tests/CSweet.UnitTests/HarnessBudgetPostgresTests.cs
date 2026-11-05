using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class HarnessBudgetPostgresTests
{
    [HarnessPostgresFact]
    public async Task MigrationAndConcurrentWireReservationsRespectOneTaskCeilingAcrossHosts()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "harness_test_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var setup = new CSweetDbContext(options);
        try
        {
            await setup.Database.MigrateAsync();
            var root = Guid.NewGuid();
            async Task<bool> Reserve()
            {
                await using var db = new CSweetDbContext(options);
                var installation = Guid.NewGuid();
                var run = new AgentRunLog { Id = Guid.NewGuid(), WorkItemId = root, Status = "Failed" };
                await using var session = new HarnessBudgetSession(db, run,
                    new(new(TaskTokenLimit: 100), "Default", null, "", []), "business", installation, 10, 10);
                try { await session.ReserveAsync(default); return true; }
                catch (InvalidOperationException error) when (error.Message.Contains("shared task budget")) { return false; }
            }
            Assert.Equal(5, (await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Reserve()))).Count(x => x));
            Assert.Equal(100, (await setup.HarnessTaskBudgets.SingleAsync()).ChargedTokens);
            Assert.Equal(5, await setup.HarnessBudgetReservations.CountAsync());
        }
        finally { Assert.StartsWith("harness_test_", connection.Database); await setup.Database.EnsureDeletedAsync(); }
    }
    private sealed class HarnessPostgresFactAttribute : FactAttribute
    {
        public HarnessPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES")))
                Skip = "Set CSWEET_DELEGATION_TEST_POSTGRES to a disposable loopback server.";
        }
    }
}
