using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthPostgresTests
{
    [HealthPostgresFact]
    public async Task OwnerReportDeliveryRollbackAndConcurrentAnswersAreAtomic()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "manager_incident_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        var clock = new HealthClock();
        await using var db = new CSweetDbContext(options, executionClock: clock);
        try
        {
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            var organization = new Organization { Id = Guid.NewGuid(), Name = "Incident regression" };
            var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id, DisplayName = "Owner",
                EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/incident-manager" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "manager",
                Version = "1.0.0", ManifestJson = """{"rolePolicy":{"baseType":"manager","profile":"manager.v1"}}""" };
            var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
                PackageVersionId = package.Id, BusinessId = organization.Id.ToString() };
            var reporter = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id, AgentInstallationId = installation.Id,
                DisplayName = "Manager", EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
            var grant = new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { ProjectHealthCapabilities.Presentation, ProjectHealthCapabilities.Resolution }),
                EventSubscriptionsJson = JsonSerializer.Serialize(new[] { ProjectHealthEvents.OwnerReportRequested }) };
            db.AddRange(organization, owner, source, package, installation, reporter, grant); await db.SaveChangesAsync();
            var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = organization.Id, Name = "Prism Break",
                Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = reporter.Id, CreatedAt = clock.Now, UpdatedAt = clock.Now };
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = organization.Id, WorkstreamId = project.Id,
                ManagerOrganizationUserId = reporter.Id, Key = "incident", CreatedAt = clock.Now };
            db.AddRange(project, board); await db.SaveChangesAsync();
            var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = organization.Id, BoardId = board.Id,
                Title = "Reconcile scope and staffing", Status = WorkTaskStatus.Blocked, BlockReason = "Update the accepted brief with recorded authority direction.",
                CreatedAt = clock.Now, UpdatedAt = clock.Now };
            db.Add(ticket); await db.SaveChangesAsync();
            var health = new ProjectHealthService(db, new(db, clock), clock);
            await health.ReviewDueAsync(default);
            var incident = await db.ProjectIncidents.SingleAsync();
            var current = await health.CurrentIncidentStateAsync(incident, default);
            var report = new IncidentOwnerReport("Scope reconciliation is blocked", "The task needs the recorded direction incorporated into its accepted brief.",
                "No confirmed recovery.", "Coordinate a brief revision.",
                [new("reconcile", "Reconcile the brief", "Coordinate the accepted direction", "Coordinate a revision with the accountable employee."),
                 new("evidence", "Collect facts", "Identify missing evidence", "Identify and request the missing logistics facts.")], "reconcile", current.Hash);
            await health.ReportAsync(organization.Id, reporter.Id, new(incident.Id, incident.Revision, "report", "Current blocker", "Recorded direction needs reconciliation",
                "Missing logistics facts", report.NextStep) { OwnerReport = report }, default);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_incident_choice() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'disposable incident choice rollback'; END $$;
                CREATE TRIGGER fail_incident_choice BEFORE INSERT ON "ExecutiveDecisions"
                FOR EACH ROW EXECUTE FUNCTION fail_incident_choice();
                """);
            await Assert.ThrowsAnyAsync<Exception>(() => health.DeliverAsync(default));
            db.ChangeTracker.Clear();
            Assert.Empty(await db.CoreConversationMessages.ToListAsync()); Assert.Empty(await db.ExecutiveDecisions.ToListAsync());
            Assert.Empty(await db.UserNotifications.ToListAsync());
            Assert.Null((await db.ProjectIncidentDeliveries.SingleAsync()).DeliveredAt);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_incident_choice ON \"ExecutiveDecisions\"; DROP FUNCTION fail_incident_choice();");
            await health.DeliverAsync(default); await health.DeliverAsync(default);
            Assert.Single(await db.CoreConversationMessages.ToListAsync()); Assert.Single(await db.ExecutiveDecisions.ToListAsync());
            var attention = Assert.Single(await health.ReadAttentionAsync(organization.Id, owner.Id, default));
            async Task<AnswerExecutiveDecisionResponse> Answer()
            {
                await using var concurrent = new CSweetDbContext(options, executionClock: clock);
                return await new ExecutiveDecisionService(concurrent, new ChatTurnService(concurrent)).AnswerAsync(organization.Id,
                    attention.ReportConversationId!.Value, attention.ResolutionDecision!.Id, owner.Id, new("reconcile", null, "stable-answer"));
            }
            var answers = await Task.WhenAll(Answer(), Answer());
            Assert.All(answers, answer => Assert.True(answer.Succeeded, answer.Message));
            Assert.Equal(answers[0].Turn!.Id, answers[1].Turn!.Id); Assert.Single(await db.ChatTurns.ToListAsync());
            Assert.Equal("Open", (await db.ProjectIncidents.AsNoTracking().SingleAsync()).Status);
            var resolution = await health.ReadResolutionAsync(organization.Id, reporter.Id, new(attention.ResolutionDecision!.Id, answers[0].Turn.Id), default);
            Assert.Contains("accountable employee", resolution.IntendedOutcome);
            // Rollout discovery closes a legacy pending card whose incident recovered,
            // while retaining an already answered choice and all original messages.
            var original = await db.ExecutiveDecisions.AsNoTracking().SingleAsync();
            var obsolete = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = organization.Id,
                ConversationId = original.ConversationId, ConversationMessageId = original.ConversationMessageId,
                RequestingInstallationId = original.RequestingInstallationId, OptionsJson = original.OptionsJson,
                IdempotencyKey = $"incident-choice:{incident.Id:N}:legacy-pending", CreatedAt = clock.Now, UpdatedAt = clock.Now };
            db.ExecutiveDecisions.Add(obsolete);
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == ticket.Id)).Status = WorkTaskStatus.Completed;
            (await db.ProjectIncidents.SingleAsync(x => x.Id == incident.Id)).Status = "Resolved";
            await db.SaveChangesAsync(); await health.ReviewDueAsync(default);
            Assert.Equal(ExecutiveDecisionStatus.Cancelled, (await db.ExecutiveDecisions.AsNoTracking().SingleAsync(x => x.Id == obsolete.Id)).Status);
            Assert.Equal(ExecutiveDecisionStatus.Answered, (await db.ExecutiveDecisions.AsNoTracking().SingleAsync(x => x.Id == original.Id)).Status);
            Assert.Empty(await health.ReadAttentionAsync(organization.Id, owner.Id, default));
            var messageCount = await db.CoreConversationMessages.CountAsync();
            await health.ReviewDueAsync(default);
            Assert.Equal(messageCount, await db.CoreConversationMessages.CountAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
