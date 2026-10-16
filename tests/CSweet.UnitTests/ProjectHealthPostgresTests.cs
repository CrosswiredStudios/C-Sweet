using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthPostgresTests
{
    [HealthPostgresFact]
    public async Task JsonScopedWorkAndFailedPlanningReachManagementWithoutInventingOwnerInput()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "project_health_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        var clock = new HealthClock();
        await using var db = new CSweetDbContext(options, executionClock: clock);
        try
        {
            await db.Database.MigrateAsync();
            var org = new Organization { Id = Guid.NewGuid(), Name = "Health regression" };
            var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Owner",
                EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/manager" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "manager",
                Version = "1.0.0", ManifestJson = """{"rolePolicy":{"baseType":"manager","profile":"manager.v1"}}""" };
            var producerInstallation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
                PackageVersionId = package.Id, BusinessId = org.Id.ToString() };
            var directorInstallation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
                PackageVersionId = package.Id, BusinessId = org.Id.ToString() };
            var director = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Creative Director",
                EmployeeType = EmployeeType.Agent, AgentInstallationId = directorInstallation.Id, ReportsToOrganizationUserId = owner.Id };
            var producer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Producer",
                EmployeeType = EmployeeType.Agent, AgentInstallationId = producerInstallation.Id, ReportsToOrganizationUserId = director.Id };
            db.AddRange(org, owner, source, package, producerInstallation, directorInstallation, director, producer);
            await db.SaveChangesAsync();
            var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org.Id, Name = "Stalled project",
                Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = producer.Id,
                CreatedAt = clock.Now, UpdatedAt = clock.Now };
            db.Add(project); await db.SaveChangesAsync();
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org.Id, WorkstreamId = project.Id,
                ManagerOrganizationUserId = producer.Id, Key = "health", CreatedAt = clock.Now };
            var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = org.Id, WorkstreamId = project.Id,
                InitiatedByOrganizationUserId = producer.Id, CreatedAt = clock.Now, UpdatedAt = clock.Now };
            db.AddRange(board, chat); await db.SaveChangesAsync();
            var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = org.Id, WorkstreamId = project.Id,
                ConversationId = chat.Id, InitiatorOrganizationUserId = producer.Id, InitiatorInstallationId = producerInstallation.Id,
                TargetOrganizationUserId = director.Id, TargetInstallationId = directorInstallation.Id,
                Subject = "Technical planning", Status = AgentCoordinationStatus.Failed, FinalSummary = "llm.request_failed",
                CreatedAt = clock.Now, UpdatedAt = clock.Now, IdempotencyKey = "planning" };
            WorkTask Task(string context, WorkTaskStatus status = WorkTaskStatus.Failed) => new() { Id = Guid.NewGuid(),
                OrganizationId = org.Id, Title = "Personal planning", PersonalWorkContextJson = context, Status = status,
                BlockReason = "Planning blocked", CreatedAt = clock.Now, UpdatedAt = clock.Now };
            var personal = Task($$"""{"workstreamId":"{{project.Id}}"}""");
            var waiting = Task($$"""{"workstreamId":"{{project.Id}}"}""", WorkTaskStatus.Running);
            waiting.WaitingReason = "Waiting for planning proposals"; waiting.NextReviewAt = clock.Now.AddHours(1);
            var failedWork = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.Id.ToString(), AgentInstallationId = producerInstallation.Id,
                SourceType = "agent-coordination", CorrelationId = session.Id.ToString(), SourceId = Guid.NewGuid().ToString(),
                Status = AgentWorkStatus.DeadLetter, AttemptCount = 1, LastError = "agent-failure:v1;code=llm.request_failed;retryable=false",
                CreatedAt = clock.Now, DeadlineAt = clock.Now.AddHours(1) };
            db.Add(failedWork);
            db.AddRange(session, personal, waiting, Task("null"), Task("{}"), Task("""{"workstreamId":null}"""),
                Task($$"""{"workstreamId":"{{Guid.NewGuid()}}","sourceFingerprint":"{{project.Id}}"}"""));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var service = new ProjectHealthService(db, new(db, clock), clock);
            await service.ReviewDueAsync(default);
            var incidents = await db.ProjectIncidents.ToListAsync();
            Assert.Equal(2, incidents.Count);
            Assert.Contains(incidents, x => x.Fingerprint == $"ticket:{personal.Id:N}");
            Assert.Contains(incidents, x => x.Fingerprint == $"coordination:{session.Id:N}");
            var deadlines = incidents.ToDictionary(x => x.Id, x => x.EscalateAt);
            await service.ReviewDueAsync(default);
            Assert.All(await db.ProjectIncidents.ToListAsync(), x => Assert.Equal(deadlines[x.Id], x.EscalateAt));
            foreach (var recipient in new[] { director.Id, owner.Id })
            {
                clock.Now += TimeSpan.FromMinutes(15);
                await service.ReviewDueAsync(default);
                Assert.All(await db.ProjectIncidents.ToListAsync(), x => Assert.Equal(recipient, x.CurrentRecipientId));
            }
            await service.DeliverAsync(default); await service.DeliverAsync(default);
            Assert.Empty(await db.CoreConversationMessages.ToListAsync());
            Assert.Empty(await service.ReadAttentionAsync(org.Id, owner.Id, default));
            Assert.Empty(await service.ReadAttentionAsync(org.Id, producer.Id, default));
            var savedPersonal = await db.CoreWorkTasks.SingleAsync(x => x.Id == personal.Id);
            savedPersonal.Status = WorkTaskStatus.Completed;
            var savedSession = await db.AgentCoordinationSessions.SingleAsync(x => x.Id == session.Id);
            savedSession.Status = AgentCoordinationStatus.Completed; savedSession.CompletedAt = clock.Now;
            await db.SaveChangesAsync(); await service.ReviewDueAsync(default);
            Assert.All(await db.ProjectIncidents.ToListAsync(), x => Assert.Equal("Resolved", x.Status));
            Assert.Empty(await service.ReadAttentionAsync(org.Id, owner.Id, default));
            await service.DeliverAsync(default);
            var notifications = await db.UserNotifications.ToListAsync();
            Assert.Empty(notifications); // Unseen incidents do not generate recovery notifications.
            // A direct conversation stays unscoped. Its interrupted turn is attributed
            // through the exact coordination source, while a cleared decision cannot
            // remain hidden by a fresh retry deadline.
            var direct = new Conversation { Id = Guid.NewGuid(), OrganizationId = org.Id,
                Kind = ConversationKind.DirectHumanAgent, InitiatedByOrganizationUserId = owner.Id,
                AgentOrganizationUserId = producer.Id, CreatedAt = clock.Now, UpdatedAt = clock.Now };
            var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = direct.Id,
                Role = ConversationRole.User, Content = "Continue this project", CreatedAt = clock.Now, SenderOrganizationUserId = owner.Id };
            db.AddRange(direct, message); await db.SaveChangesAsync();
            var interrupted = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = org.Id, ConversationId = direct.Id,
                TargetAgentOrganizationUserId = producer.Id, UserMessageId = message.Id,
                Status = ChatTurnStatus.Running, CreatedAt = clock.Now, UpdatedAt = clock.Now };
            db.Add(interrupted); await db.SaveChangesAsync();
            (await db.AgentCoordinationSessions.SingleAsync()).SourceChatTurnId = interrupted.Id;
            var decision = new WorkstreamDecisionRecord { Id = Guid.NewGuid(), OrganizationId = org.Id,
                WorkstreamId = project.Id, RequestedByOrganizationUserId = owner.Id, Status = "Decided",
                UpdatedAt = clock.Now.AddMinutes(-16), IdempotencyKey = "cleared-decision" };
            var dependency = await db.CoreWorkTasks.SingleAsync(x => x.Id == waiting.Id);
            dependency.WaitingReason = "Waiting for decision"; dependency.NextReviewAt = clock.Now.AddHours(1);
            dependency.PersonalWorkContextJson = $$"""{"workstreamId":"{{project.Id}}","decisionId":"{{decision.Id}}"}""";
            db.Add(decision); await db.SaveChangesAsync();
            interrupted.Status = ChatTurnStatus.CompletedWithWarnings; interrupted.ErrorCode = "memory.runtime_reset";
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            await service.ReviewDueAsync(default);
            var newIncidents = await db.ProjectIncidents.Where(x => x.Status == "Open").ToListAsync();
            Assert.Equal(2, newIncidents.Count);
            Assert.Contains(newIncidents, x => x.Fingerprint == $"chat:{interrupted.Id:N}");
            Assert.Contains(newIncidents, x => x.Fingerprint == $"dependency-wait:{waiting.Id:N}");
            foreach (var recipient in new[] { director.Id, owner.Id })
            {
                clock.Now += TimeSpan.FromMinutes(15); await service.ReviewDueAsync(default);
                Assert.All(await db.ProjectIncidents.Where(x => x.Status == "Open").ToListAsync(), x => Assert.Equal(recipient, x.CurrentRecipientId));
            }
            await service.DeliverAsync(default);
            Assert.Empty(await service.ReadAttentionAsync(org.Id, owner.Id, default));
            dependency = await db.CoreWorkTasks.SingleAsync(x => x.Id == waiting.Id);
            dependency.Status = WorkTaskStatus.Completed; dependency.WaitingReason = null;
            var retryMessage = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = direct.Id,
                Role = ConversationRole.User, Content = "Retry the interrupted request", CreatedAt = clock.Now, SenderOrganizationUserId = owner.Id };
            db.Add(retryMessage); await db.SaveChangesAsync();
            db.Add(new ChatTurn { Id = Guid.NewGuid(), OrganizationId = org.Id, ConversationId = direct.Id,
                TargetAgentOrganizationUserId = producer.Id, UserMessageId = retryMessage.Id,
                RetryOfTurnId = interrupted.Id, Status = ChatTurnStatus.Completed, CreatedAt = clock.Now, UpdatedAt = clock.Now });
            await db.SaveChangesAsync(); (await db.ProjectHealthStates.SingleAsync()).NextReviewAt = clock.Now;
            await db.SaveChangesAsync(); await service.ReviewDueAsync(default);
            Assert.All(await db.ProjectIncidents.ToListAsync(), x => Assert.Equal("Resolved", x.Status));
            Assert.Empty(await service.ReadAttentionAsync(org.Id, owner.Id, default));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class HealthClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [HealthPostgresFact]
    public async Task IncidentAndOutboxRollbackTogetherAndOpenEpisodeIsUnique()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = "project_health_test_" + Guid.NewGuid().ToString("N"); settings.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            var org = Guid.NewGuid(); var project = Guid.NewGuid(); var actor = Guid.NewGuid();
            ProjectIncident NewIncident() => new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project,
                ProducerEmployeeId = actor, CurrentRecipientId = actor, Fingerprint = "idle", DetectedAt = DateTimeOffset.UtcNow,
                LastProgressAt = DateTimeOffset.UtcNow, EscalateAt = DateTimeOffset.UtcNow.AddMinutes(15) };
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.ProjectIncidents.Add(NewIncident());
                db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, EventType = "test",
                    IdempotencyKey = Guid.NewGuid().ToString(), OccurredAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
                db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = org,
                    EventType = CSweet.Contracts.Realtime.AppRealtimeEvents.ProjectIncidentChanged,
                    Subject = $"organizations/{org}/projects/{project}", OccurredAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(); await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Empty(await db.ProjectIncidents.ToListAsync()); Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync()); Assert.Empty(await db.ApplicationRealtimeOutbox.ToListAsync());
            db.ProjectIncidents.Add(NewIncident()); await db.SaveChangesAsync();
            db.ProjectIncidents.Add(NewIncident()); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            var existing = await db.ProjectIncidents.SingleAsync(); existing.Status = "Resolved"; await db.SaveChangesAsync();
            db.ProjectIncidents.Add(NewIncident()); await db.SaveChangesAsync(); Assert.Equal(2, await db.ProjectIncidents.CountAsync());
            await using var other = new CSweetDbContext(options);
            var concurrent = await other.ProjectIncidents.SingleAsync(x => x.Status == "Open");
            var current = await db.ProjectIncidents.SingleAsync(x => x.Status == "Open");
            current.Revision++; await db.SaveChangesAsync(); concurrent.Revision++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class HealthPostgresFactAttribute : FactAttribute
    {
        public HealthPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a disposable loopback PostgreSQL server.";
        }
    }
}
