using CSweet.Application.Setup;
using CSweet.Contracts.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class EmployeeAuditPostgresTests
{
    [AuditPostgresFact]
    public async Task MigrationRollbackDeliveryAndHistoricalQueriesUseOneLedger()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "audit_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        var protection = new EphemeralDataProtectionProvider();
        await using var db = new CSweetDbContext(options, protection);
        try
        {
            await db.Database.MigrateAsync();
            var org = Guid.NewGuid(); var employee = Guid.NewGuid();
            AgentRunLog Run() => new() { Id = Guid.NewGuid(), OrganizationId = org, EmployeeId = employee, AgentKey = "test", ProviderProfileId = Guid.NewGuid(), Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.Add(Run()); await db.SaveChangesAsync();
                Assert.Equal(1, await db.AuditOutbox.CountAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Empty(await db.AgentRunLogs.ToListAsync()); Assert.Empty(await db.AuditOutbox.ToListAsync());
            db.Add(Run()); await db.SaveChangesAsync();
            await using var services = new ServiceCollection().AddScoped(_ => new CSweetDbContext(options, protection)).BuildServiceProvider();
            var writer = new AuditEventWriter(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
            await new AuditOutboxDispatcher(db, writer, TimeProvider.System).DispatchAsync(default);
            var reader = new SecurityAuditService(db, protection);
            var page = await reader.BrowseAsync(org, new(EmployeeId: employee, OrderByOccurrence: true));
            var item = Assert.Single(page.Items); Assert.Equal("Verified", item.IntegrityStatus);
            var detail = (await reader.GetAsync(org, item.Id))!;
            Assert.Equal("Verified", detail.IntegrityStatus); Assert.Equal("Available", detail.PayloadAvailability);
            Assert.Empty((await reader.BrowseAsync(org, new(From: DateTimeOffset.UtcNow.AddDays(1), EmployeeId: employee, OrderByOccurrence: true))).Items);
            // Executes the bounded SQL anti-joins for every historical source.
            Assert.Equal(0, await new AuditHistoryImporter(db).ImportBatchAsync(default));
            var second = await writer.AppendAsync(new("late.record", OrganizationId: org, OccurredAt: item.OccurredAt.AddDays(-1), Employees: [new(employee)]));
            var firstPage = await reader.BrowseAsync(org, new(Limit: 1, EmployeeId: employee, OrderByOccurrence: true));
            Assert.Equal(second, Assert.Single((await reader.BrowseAsync(org, new(Cursor: firstPage.NextCursor, EmployeeId: employee, OrderByOccurrence: true))).Items).Id);
            var modelRunId = Guid.NewGuid();
            for (var i = 0; i < 3; i++)
                await writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: org, EntityType: "AgentRunLog", EntityId: modelRunId,
                    OccurredAt: DateTimeOffset.UtcNow, Employees: [new(employee)], ContentType: "application/json",
                    Payload: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { sequence = i, text = "word", contents = Array.Empty<object>() })));
            var grouped = await reader.BrowseAsync(org, new(Limit: 1, EmployeeId: employee, OrderByOccurrence: true, GroupModelResponses: true));
            var responseItem = Assert.Single(grouped.Items);
            Assert.Equal("model.response", responseItem.EventType); Assert.Equal(3, responseItem.ModelResponseChunkCount);
            Assert.Equal("wordwordword", (await reader.GetAsync(org, responseItem.Id, groupModelResponse: true, employeeId: employee))!.ModelResponse!.Text);
            Assert.DoesNotContain((await reader.BrowseAsync(org, new(Cursor: grouped.NextCursor, EmployeeId: employee, OrderByOccurrence: true, GroupModelResponses: true))).Items, x => x.EntityId == modelRunId);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class AuditPostgresFactAttribute : FactAttribute
    {
        public AuditPostgresFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"))) Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a disposable loopback PostgreSQL server."; }
    }
}
