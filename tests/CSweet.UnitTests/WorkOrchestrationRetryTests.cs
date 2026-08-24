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
