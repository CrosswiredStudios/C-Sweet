using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wire = CSweet.WorkManagement.Contracts;
using Session = CSweet.Agent.SDK.AgentCoordinationSession;

namespace CSweet.UnitTests;

public sealed class WorkIssuePostgresTests
{
    [IssuePostgresFact]
    public async Task MigrationPreservesLegacyWorkAndInterruptedDispatchRollsBackSessionCardAndOutboxThenRecoversOnce()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost" });
        settings.Database = "issues_test_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{settings.Database}\"", admin)) await create.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        try
        {
            await using var f = new WorkIssueLifecycleTests.Fixture(options);
            await f.Db.Database.EnsureCreatedAsync(); await f.Db.Database.OpenConnectionAsync();
            // These fixtures isolate work/state transactions. Unrelated organization/install FKs are disabled on
            // this disposable connection; unique indexes, concurrency checks and transactions remain enforced.
            await f.Db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await f.SeedAsync();
            var retained = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, Title = "Existing Brickwave planning work" };
            f.Db.Add(retained); await f.Db.SaveChangesAsync();
            await f.Db.Database.ExecuteSqlRawAsync("DROP TABLE \"WorkIssueCases\", \"WorkIssueCommissions\", \"DurableMutationReceipts\"");
            foreach (var sql in f.Db.GetService<IMigrationsSqlGenerator>().Generate(new DurableWorkIssues().UpOperations, f.Db.Model))
                await f.Db.Database.ExecuteSqlRawAsync(sql.CommandText);
            Assert.Equal(retained.Title, (await f.Db.CoreWorkTasks.AsNoTracking().SingleAsync()).Title);
            Assert.False(f.Db.Database.HasPendingModelChanges());
            var issue = Assert.Single((await f.Save("Review the approved audio responsibility", "case")).Issues);
            var request = new Wire.CommissionWorkIssuesRequest(f.Board.Id, f.Specialist.Id, [issue.Id], f.Source, "Reconcile the retained responsibility", "commission");
            var intent = await f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId!.Value, request, default);
            var before = await f.Db.AgentPlatformEventOutbox.CountAsync();
            var interrupt = true;
            var coordination = Proxy<IAgentCoordinationService>((method, args) => method switch {
                "StartBoardAsync" => Start(),
                "ReadAsync" => Read(),
                "CancelAsync" => Cancel(),
                _ => throw new NotSupportedException(method)
            });
            var todos = Proxy<IPersonalTodoService>((method, args) => method switch {
                "AddAsync" => Add(), "ListAsync" => List(), "ArchiveAsync" => Archive((Wire.ArchivePersonalTodoItemRequest)args![2]!),
                _ => throw new NotSupportedException(method)
            });
            var dispatcher = new WorkIssueCommissionDispatcher(f.Db, f.Service, coordination, todos, TimeProvider.System);
            await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchOneAsync(intent.Id, default));
            f.Db.ChangeTracker.Clear();
            Assert.Empty(await f.Db.AgentCoordinationSessions.ToListAsync());
            Assert.Single(await f.Db.CoreWorkTasks.ToListAsync());
            Assert.Equal(before, await f.Db.AgentPlatformEventOutbox.CountAsync());
            Assert.Null((await f.Db.WorkIssueCommissions.SingleAsync()).SessionId);
            interrupt = false;
            await dispatcher.DispatchOneAsync(intent.Id, default);
            await dispatcher.DispatchOneAsync(intent.Id, default); // duplicate wake
            Assert.Single(await f.Db.AgentCoordinationSessions.ToListAsync());
            Assert.Equal(2, await f.Db.CoreWorkTasks.CountAsync());
            Assert.Equal("Dispatched", (await f.Db.WorkIssueCommissions.SingleAsync()).Status);
            Assert.Equal(intent.Id, (await f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId.Value, request, default)).Id);
            var open = await f.Db.WorkIssueCases.SingleAsync(); Assert.Equal("Open", open.Status);
            var sharedSession = (await f.Db.AgentCoordinationSessions.SingleAsync()).Id;
            var other = Assert.Single((await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
                [new("another-responsibility", "Another scope still needs this advice", "Preserve meaningful independent responsibility", ["other"]) {
                    Links = [new("Coordination", sharedSession)] }], "other-open"), default)).Issues);
            var leased = new CSweet.Domain.Setup.AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
                AgentInstallationId = f.Specialist.AgentInstallationId!.Value, Status = CSweet.Domain.Setup.AgentWorkStatus.Leased };
            f.Db.Add(leased); (await f.Db.AgentCoordinationSessions.SingleAsync()).CurrentAgentWorkItemId = leased.Id; await f.Db.SaveChangesAsync();
            await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
                [new(open.ObligationKey, open.Statement, open.Detail, ["audio"]) { IssueId = open.Id, ExpectedRevision = open.Revision,
                    Status = "Resolved", ResolutionEvidence = "Reviewed ownership now covers the exact obligation" }], "resolve"), default);
            var retirement = await f.Db.WorkIssueCommissions.SingleAsync(c => c.Status == "RetirementPending");
            await dispatcher.DispatchOneAsync(retirement.Id, default);
            Assert.Equal("RetirementPending", retirement.Status); Assert.Equal(AgentCoordinationStatus.Active, (await f.Db.AgentCoordinationSessions.SingleAsync()).Status);
            leased.Status = CSweet.Domain.Setup.AgentWorkStatus.Completed; await f.Db.SaveChangesAsync();
            await dispatcher.DispatchOneAsync(retirement.Id, default);
            Assert.Equal("RetirementPending", retirement.Status); // Another obligation still needs the shared advice.
            await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
                [new(other.ObligationKey, other.Statement, other.Detail, other.ScopeKeys) { IssueId = other.Id, ExpectedRevision = other.Revision,
                    Status = "Resolved", ResolutionEvidence = "The second responsibility has now been reviewed and owned" }], "other-resolved"), default);
            leased.Status = CSweet.Domain.Setup.AgentWorkStatus.Leased; await f.Db.SaveChangesAsync();
            await dispatcher.DispatchOneAsync(retirement.Id, default);
            Assert.Equal("RetirementPending", retirement.Status);
            leased.Status = CSweet.Domain.Setup.AgentWorkStatus.Completed; await f.Db.SaveChangesAsync();
            await dispatcher.DispatchOneAsync(retirement.Id, default);
            Assert.Equal("Retired", retirement.Status); Assert.Equal(AgentCoordinationStatus.Cancelled, (await f.Db.AgentCoordinationSessions.SingleAsync()).Status);
            Assert.Single(await f.Db.CoreWorkTasks.Where(t => t.ArchivedAt != null).ToListAsync());
            Assert.All(await f.Db.WorkIssueCases.ToListAsync(), c => Assert.Equal("Resolved", c.Status));

            async Task<Session> Start()
            {
                var row = new CSweet.Domain.Communications.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
                    WorkstreamId = f.Board.WorkstreamId, InitiatorOrganizationUserId = f.Manager.Id, TargetOrganizationUserId = f.Specialist.Id };
                f.Db.Add(row); await f.Db.SaveChangesAsync();
                return Frame(row);
            }
            async Task<Session> Read() => Frame(await f.Db.AgentCoordinationSessions.SingleAsync());
            Session Frame(CSweet.Domain.Communications.AgentCoordinationSession row)
            {
                var input = new WorkContributionRequest(f.Board.WorkstreamId!.Value, f.Board.Id, "retained", request.Question,
                    [new(f.Source.DocumentId, f.Source.RevisionId, f.Source.Digest)]);
                return JsonSerializer.Deserialize<Session>("{}")! with { Id = row.Id, Revision = row.Revision, Status = row.Status.ToString(), BoardSource = new(f.Board.Id),
                    Initiator = new(f.Manager.Id, f.Manager.AgentInstallationId!.Value, "Manager", "Manager"),
                    Target = new(f.Specialist.Id, f.Specialist.AgentInstallationId!.Value, "Specialist", "Specialist"),
                    Turns = [new(Guid.NewGuid(), 1, f.Manager.Id, "Request", "Continue", DateTimeOffset.UtcNow,
                        new(WorkContributions.RequestType, "1.0", "retained", 1, true, JsonSerializer.SerializeToElement(input), "request"))] };
            }
            async Task<Session> Cancel()
            {
                var row = await f.Db.AgentCoordinationSessions.SingleAsync(); row.Status = AgentCoordinationStatus.Cancelled; row.Revision++; await f.Db.SaveChangesAsync(); return Frame(row);
            }
            async Task<Wire.PersonalTodoDirectory> List()
            {
                var row = await f.Db.CoreWorkTasks.SingleAsync(t => t.Id != retained.Id);
                return new([new(Guid.NewGuid(), f.Manager.Id, "Manager", null, null, 1,
                    [JsonSerializer.Deserialize<Wire.PersonalTodoItem>("{}")! with { Id = row.Id, Revision = row.Revision, Status = Wire.PersonalTodoStatuses.Blocked,
                        CorrelationId = TrackedCollaboration.Correlation((await f.Db.AgentCoordinationSessions.SingleAsync()).Id),
                        WorkContext = new(CoordinationSessionId: (await f.Db.AgentCoordinationSessions.SingleAsync()).Id) }])]);
            }
            async Task<Wire.PersonalTodoItem> Archive(Wire.ArchivePersonalTodoItemRequest input)
            {
                var row = await f.Db.CoreWorkTasks.SingleAsync(t => t.Id == input.ItemId); row.ArchivedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
                return JsonSerializer.Deserialize<Wire.PersonalTodoItem>("{}")! with { Id = row.Id };
            }
            async Task<Wire.PersonalTodoItem> Add()
            {
                var row = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, Title = "Track scoped advice" };
                f.Db.Add(row); await f.Db.SaveChangesAsync();
                if (interrupt) throw new IOException("Process interrupted after session/card SQL, before binding and commit.");
                return JsonSerializer.Deserialize<Wire.PersonalTodoItem>("{}")! with { Id = row.Id };
            }
        }
        finally
        {
            Assert.StartsWith("issues_test_", settings.Database);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{settings.Database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
        }
    }
    private static T Proxy<T>(Func<string,object?[]?,object> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>(); ((TestProxy)(object)proxy).InvokeMethod = invoke; return proxy;
    }
    public class TestProxy : DispatchProxy
    {
        public Func<string,object?[]?,object> InvokeMethod { get; set; } = null!;
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!.Name, args);
    }
    private sealed class IssuePostgresFactAttribute : FactAttribute
    {
        public IssuePostgresFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"))) Skip = "Set CSWEET_COMPUTE_TEST_DATABASE for isolated PostgreSQL transactions."; }
    }
}
