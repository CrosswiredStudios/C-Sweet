using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class ResourceChangeServiceTests
{
    [StaffingPostgresFact]
    public async Task Staffing_decision_and_exact_wait_wake_commit_atomically_and_recover_after_interruption()
    {
        await InStaffingDatabase(async settings =>
        {
            var fail = new InterruptDecisionWrite();
            var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings).AddInterceptors(fail).Options;
            await using var db = new CSweetDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            var setup = SeedManagerConversation(db);
            setup.UserMessage.Sequence = 0; // Let PostgreSQL allocate the identity just as a production chat write does.
            await db.SaveChangesAsync();
            var resources = new ResourceChangeService(db, new TestAuditEventWriter());
            var proposal = await resources.ProposeAsync(setup.OrganizationId, setup.RequesterInstallationId, Proposal(setup, "atomic-review"));
            var work = new PersonalTodoService(db, TimeProvider.System);
            var actor = new PersonalTodoActor(setup.RequesterId, setup.RequesterInstallationId);
            var task = await work.AddAsync(setup.OrganizationId, actor, new("Review staffing", null, "High", null, "atomic-wait")
                { WorkContext = new() });
            var independent = await work.AddAsync(setup.OrganizationId, actor, new("Draft independent evidence", null, "Medium", null, "independent"));
            var stored = await db.CoreWorkTasks.SingleAsync(t => t.Id == task.Id);
            var claim = Guid.NewGuid(); stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = claim;
            stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2); stored.Revision++;
            await db.SaveChangesAsync();
            await work.DeferAsync(setup.OrganizationId, actor, new(task.Id, claim, stored.Revision,
                DateTimeOffset.UtcNow.AddHours(1), "Review pending", null, "wait-current-review")
                { Dependency = new(Wire.PersonalTodoDependencyKinds.ResourceChange, proposal.Id, "Pending") });
            var decision = new ResourceChangeDecisionRequest(proposal.Id, "RequestRevision", "Complete the technical accountability", "atomic-decision");
            fail.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => resources.DecideForInstallationAsync(setup.OrganizationId, setup.ManagerInstallationId, decision));
            db.ChangeTracker.Clear();
            Assert.Equal(ResourceChangeRequestStatus.Pending, (await db.ResourceChangeRequests.SingleAsync()).Status);
            Assert.Equal(WorkTaskStatus.Running, (await db.CoreWorkTasks.SingleAsync(t => t.Id == task.Id)).Status);
            Assert.False(await db.AgentPlatformEventOutbox.AnyAsync(e => e.EventType == ResourceChangeEvents.Decided));
            await resources.DecideForInstallationAsync(setup.OrganizationId, setup.ManagerInstallationId, decision);
            var resumed = await db.CoreWorkTasks.SingleAsync(t => t.Id == task.Id);
            Assert.Equal(WorkTaskStatus.Ready, resumed.Status); Assert.Null(resumed.WaitingDependencyJson);
            Assert.Single(await db.AgentPlatformEventOutbox.Where(e => e.EventType == ResourceChangeEvents.Decided).ToArrayAsync());
            var wakeCount = await db.AgentPlatformEventOutbox.CountAsync(e => e.EventType == Wire.PersonalTodoEvents.Available);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                await resources.DecideForInstallationAsync(setup.OrganizationId, setup.ManagerInstallationId, decision);
                await work.ReconcileAsync();
            }
            Assert.Equal(wakeCount, await db.AgentPlatformEventOutbox.CountAsync(e => e.EventType == Wire.PersonalTodoEvents.Available));
            Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.SingleAsync(t => t.Id == independent.Id)).Status);
        });
    }

    [StaffingPostgresFact]
    public async Task Staffing_upgrade_preserves_existing_records_and_accepts_full_unicode_narratives()
    {
        await InStaffingDatabase(async settings =>
        {
            await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings).Options);
            await db.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var migration = new StaffingReviewDependenciesAndNarrativeText();
            var alterations = migration.UpOperations.OfType<AlterColumnOperation>().ToArray();
            foreach (var table in alterations.GroupBy(c => c.Table))
            {
                var columns = string.Join(", ", table.Select(c => $"{Quote(c.Name)} {c.OldColumn.ColumnType}"));
                if (table.Key == "ResourceChangeRequests") columns += ", \"OrganizationId\" uuid, \"RequesterOrganizationUserId\" uuid";
                await Sql($"CREATE TABLE {Quote(table.Key)} (\"Id\" integer PRIMARY KEY, {columns})");
                await Sql($"INSERT INTO {Quote(table.Key)} (\"Id\", {string.Join(", ", table.Select(c => Quote(c.Name)))}) VALUES (1, {string.Join(", ", table.Select(_ => "'Existing Brickfall record'"))})");
            }
            await Sql("CREATE TABLE \"CoreWorkTasks\" (\"Id\" integer PRIMARY KEY)");
            foreach (var sql in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.Model)) await Sql(sql.CommandText);
            var text = string.Concat(Enumerable.Repeat("完整の staffing responsibility 🎮\n", 600));
            foreach (var column in alterations)
            {
                await using var read = new NpgsqlCommand($"SELECT {Quote(column.Name)} FROM {Quote(column.Table)} WHERE \"Id\"=1", connection);
                Assert.Equal("Existing Brickfall record", await read.ExecuteScalarAsync());
                await using var write = new NpgsqlCommand($"UPDATE {Quote(column.Table)} SET {Quote(column.Name)}=@text WHERE \"Id\"=1", connection);
                write.Parameters.AddWithValue("text", text); await write.ExecuteNonQueryAsync();
                Assert.Equal(text, await read.ExecuteScalarAsync());
            }
            Assert.False(db.Database.HasPendingModelChanges());
            async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
        });
    }

    private sealed class InterruptDecisionWrite : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (Armed && data.Context!.ChangeTracker.Entries<AgentPlatformEventOutboxItem>().Any(e => e.Entity.EventType == ResourceChangeEvents.Decided))
            { Armed = false; throw new IOException("Interrupted immediately before the atomic state/outbox write"); }
            return ValueTask.FromResult(result);
        }
    }

    private static async Task InStaffingDatabase(Func<string, Task> test)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "staffing_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {Quote(settings.Database)}", admin)) await create.ExecuteNonQueryAsync();
        try { await test(settings.ConnectionString); }
        finally
        {
            Assert.StartsWith("staffing_test_", settings.Database);
            await using var drop = new NpgsqlCommand($"DROP DATABASE {Quote(settings.Database)} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private sealed class StaffingPostgresFactAttribute : FactAttribute
    {
        public StaffingPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE for disposable local PostgreSQL coverage.";
        }
    }
}
