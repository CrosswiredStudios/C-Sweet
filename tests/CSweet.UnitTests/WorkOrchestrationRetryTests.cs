using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed class WorkOrchestrationRetryTests
{
    [Fact]
    public async Task ExactBlockedStageRetry_IsIdempotentAndPreservesAttemptBudget()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var seeded = await SeedAsync(db);
        var service = new WorkOrchestrationService(db, TimeProvider.System);
        var request = new WorkOrchestrationControlRequest(
            seeded.AssignmentRevision, "retry-stage-1", "Architect guidance was consumed.");

        var first = await service.RetryAsync(
            seeded.OrganizationId, seeded.BoardId, seeded.StageId,
            seeded.DeveloperInstallationId, request);
        var replay = await service.RetryAsync(
            seeded.OrganizationId, seeded.BoardId, seeded.StageId,
            seeded.DeveloperInstallationId, request);

        Assert.Equal("Pending", first.Status);
        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x =>
            x.EventType == "stage.retry.requested" && x.IdempotencyKey == request.IdempotencyKey)
            .ToListAsync());
    }

    [Fact]
    public async Task ExactBlockedStageRetry_FailsClosedWhenAssignmentRevisionIsStale()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var seeded = await SeedAsync(db);
        var service = new WorkOrchestrationService(db, TimeProvider.System);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RetryAsync(
            seeded.OrganizationId, seeded.BoardId, seeded.StageId,
            seeded.DeveloperInstallationId,
            new WorkOrchestrationControlRequest(
                seeded.AssignmentRevision - 1, "stale-retry", "Stale guidance.")));
    }

    [Fact]
    public async Task GameTeamRetry_UsesAssignmentAuthorityInsteadOfSoftwareRoleNames()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var seeded = await SeedAsync(db);
        foreach (var role in await db.CoreRoles.ToListAsync())
            role.Name = role.Name switch
            {
                "Software Developer" => "Video Game Engineer",
                "Software Architect" => "Video Game Technical Director",
                _ => "Video Game Quality Assurance"
            };
        await db.SaveChangesAsync();
        var service = new WorkOrchestrationService(db, TimeProvider.System);

        var result = await service.RetryAsync(seeded.OrganizationId, seeded.BoardId,
            seeded.StageId, seeded.DeveloperInstallationId,
            new WorkOrchestrationControlRequest(seeded.AssignmentRevision, "game-retry", "Technical guidance applied."));

        Assert.Equal("Pending", result.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(WorkStageExecutionStatus.Pending, (await db.WorkStageExecutions.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("archived")]
    [InlineData("unassigned")]
    public async Task Retry_RejectsLostTeamOrAssignmentAuthority(string condition)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var seeded = await SeedAsync(db);
        var caller = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == seeded.DeveloperInstallationId);
        if (condition == "membership")
            (await db.TeamMemberships.SingleAsync(x => x.OrganizationUserId == caller.Id)).EndedAt = DateTimeOffset.UtcNow;
        else if (condition == "archived")
            (await db.OrganizationTeams.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow;
        else
            (await db.WorkStageExecutions.SingleAsync()).AgentInstallationId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var service = new WorkOrchestrationService(db, TimeProvider.System);
        var error = await Record.ExceptionAsync(() => service.RetryAsync(seeded.OrganizationId,
            seeded.BoardId, seeded.StageId, seeded.DeveloperInstallationId,
            new WorkOrchestrationControlRequest(seeded.AssignmentRevision, "denied-retry", "Retry requested.")));
        if (condition == "archived") Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<UnauthorizedAccessException>(error);
        db.ChangeTracker.Clear();
        Assert.Equal(WorkStageExecutionStatus.Blocked, (await db.WorkStageExecutions.SingleAsync()).Status);
        Assert.Empty(await db.WorkOrchestrationEvents.ToListAsync());
    }

    [Fact]
    public async Task RelationalRetryLoadsLongCheckpointHistoryOnceAndPreservesEveryAttempt()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var capture = new RetryQueries();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseSqlite(connection).AddInterceptors(capture, new RetrySqliteSequence()).Options);
        await db.Database.EnsureCreatedAsync();
        var seeded = await SeedAsync(db);
        for (var number = 1; number <= 500; number++)
            db.WorkExecutionAttempts.Add(new() { Id = Guid.NewGuid(), StageExecutionId = seeded.StageId,
                Attempt = number, Status = WorkExecutionAttemptStatus.Completed,
                ResultJson = "{}", IdempotencyKey = "checkpoint-" + number, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); capture.Commands.Clear();

        var result = await new WorkOrchestrationService(db, TimeProvider.System).RetryAsync(
            seeded.OrganizationId, seeded.BoardId, seeded.StageId, seeded.DeveloperInstallationId,
            new WorkOrchestrationControlRequest(seeded.AssignmentRevision, "long-history-retry"));

        Assert.Equal("Pending", result.Status);
        Assert.Equal(500, result.AttemptCount);
        var stageLoads = capture.Commands.Where(sql =>
            sql.Contains("FROM \"WorkStageExecutions\"", StringComparison.Ordinal) &&
            sql.Contains("\"WorkExecutionAttempts\"", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(stageLoads);
        Assert.All(stageLoads, sql => Assert.Equal(1,
            System.Text.RegularExpressions.Regex.Matches(sql, "\"WorkExecutionAttempts\"").Count));
        db.ChangeTracker.Clear();
        Assert.Equal(500, await db.WorkExecutionAttempts.CountAsync());
        Assert.Single(await db.WorkOrchestrationEvents.Where(x => x.EventType == "stage.retry.requested").ToListAsync());
    }

    private sealed class RetrySqliteSequence : SaveChangesInterceptor
    {
        private long sequence;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            // SQLite cannot generate the PostgreSQL outbox identity column.
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<CSweet.Domain.Notifications.ApplicationRealtimeOutboxItem>()
                         .Where(x => x.State == EntityState.Added && x.Entity.Sequence == 0))
                entry.Entity.Sequence = ++sequence;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RetryQueries : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken token = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<Seeded> SeedAsync(CSweetDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var developerId = Guid.NewGuid();
        var developerInstallationId = Guid.NewGuid();
        var architectId = Guid.NewGuid();
        var qaId = Guid.NewGuid();
        var policyRevisionId = Guid.NewGuid();
        var sprintExecutionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemExecutionId = Guid.NewGuid();
        var stageId = Guid.NewGuid();
        const long assignmentRevision = 4;

        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Delivery company", Status = OrganizationStatus.Active,
            CreatedAt = now, UpdatedAt = now
        });
        var developerRole = Role(organizationId, "Software Developer", now);
        var architectRole = Role(organizationId, "Software Architect", now);
        var qaRole = Role(organizationId, "Software QA", now);
        db.CoreRoles.AddRange(developerRole, architectRole, qaRole);
        db.CoreOrganizationUsers.AddRange(
            AgentUser(developerId, organizationId, developerInstallationId, developerRole.Id, "Developer", now),
            AgentUser(architectId, organizationId, Guid.NewGuid(), architectRole.Id, "Architect", now),
            AgentUser(qaId, organizationId, Guid.NewGuid(), qaRole.Id, "QA", now));
        db.OrganizationTeams.Add(new OrganizationTeam
        {
            Id = teamId, OrganizationId = organizationId, TeamKey = "DELIVERY",
            NormalizedName = "DELIVERY", Name = "Delivery", LeadOrganizationUserId = developerId,
            CreatedAt = now, UpdatedAt = now
        });
        db.TeamMemberships.AddRange(
            Membership(organizationId, teamId, developerId, now),
            Membership(organizationId, teamId, architectId, now),
            Membership(organizationId, teamId, qaId, now));
        db.WorkBoards.Add(new WorkBoard
        {
            Id = boardId, OrganizationId = organizationId, TeamId = teamId,
            ManagerOrganizationUserId = architectId, Key = "DELIVERY", Name = "Delivery",
            CreatedAt = now, UpdatedAt = now
        });
        db.CoreWorkTasks.Add(new WorkTask
        {
            Id = itemId, OrganizationId = organizationId, BoardId = boardId,
            AssignmentRevision = assignmentRevision, Title = "Implement slice", Description = "",
            Status = WorkTaskStatus.Blocked, Priority = WorkTaskPriority.High,
            BoardRank = 1024, CreatedAt = now, UpdatedAt = now
        });
        db.WorkOrchestrationStages.Add(new WorkOrchestrationStage
        {
            Id = Guid.NewGuid(), PolicyRevisionId = policyRevisionId, Key = "development",
            Name = "Development", Type = WorkOrchestrationStageType.AgentExecution,
            MaximumAttempts = 3
        });
        db.WorkSprintExecutions.Add(new WorkSprintExecution
        {
            Id = sprintExecutionId, OrganizationId = organizationId, BoardId = boardId,
            SprintId = Guid.NewGuid(), PolicyRevisionId = policyRevisionId,
            StartedByOrganizationUserId = architectId, Status = WorkSprintExecutionStatus.Active,
            StartedAt = now, UpdatedAt = now
        });
        db.WorkItemExecutions.Add(new WorkItemExecution
        {
            Id = itemExecutionId, SprintExecutionId = sprintExecutionId, WorkItemId = itemId,
            ItemIdentifier = "DELIVERY-1", CurrentStageKey = "development",
            Status = WorkItemExecutionStatus.Blocked, BlockedReason = "Compilation failed.",
            CreatedAt = now, UpdatedAt = now
        });
        db.WorkStageExecutions.Add(new WorkStageExecution
        {
            Id = stageId, ItemExecutionId = itemExecutionId, StageKey = "development",
            StageType = WorkOrchestrationStageType.AgentExecution,
            Status = WorkStageExecutionStatus.Blocked,
            PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            AgentInstallationId = developerInstallationId, LastError = "Compilation failed.",
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(organizationId, boardId, stageId, developerInstallationId, assignmentRevision);
    }

    private static Role Role(Guid organizationId, string name, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, Name = name,
        Description = name, AuthorityLevel = AuthorityLevel.ExecutionWithApproval,
        CreatedAt = now, UpdatedAt = now
    };

    private static OrganizationUser AgentUser(
        Guid id, Guid organizationId, Guid installationId, Guid roleId, string name,
        DateTimeOffset now) => new()
    {
        Id = id, OrganizationId = organizationId, AgentInstallationId = installationId,
        RoleId = roleId, DisplayName = name, EmployeeType = EmployeeType.Agent,
        PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true, CreatedAt = now
    };

    private static TeamMembership Membership(
        Guid organizationId, Guid teamId, Guid organizationUserId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, TeamId = teamId,
        OrganizationUserId = organizationUserId, ExclusiveAgentEmployeeId = organizationUserId,
        SourceType = "Test", JoinedAt = now
    };

    private sealed record Seeded(
        Guid OrganizationId, Guid BoardId, Guid StageId, Guid DeveloperInstallationId,
        long AssignmentRevision);
}
