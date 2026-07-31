using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using DomainSprint = CSweet.Domain.WorkManagement.WorkSprint;
using DomainColumn = CSweet.Domain.WorkManagement.WorkBoardColumn;

namespace CSweet.UnitTests;

public sealed class WorkDeliveryPipelineServiceTests
{
    [Fact]
    public async Task Pulse_StartsLowestSequenceAndAssignsOneEligibleTicket()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var runtime = new RuntimeStub();
        var service = CreateService(db, runtime);

        Assert.Equal(1, await service.PulseAsync());
        Assert.Equal(WorkSprintStatus.Active, setup.FirstSprint.Status);
        Assert.Equal(WorkSprintStatus.Planned, setup.SecondSprint.Status);

        Assert.Equal(1, await service.PulseAsync());
        Assert.Equal(setup.FirstTicket.Id, setup.Pipeline.ActiveWorkItemId);
        Assert.Equal("Development", setup.Pipeline.Stage);
        Assert.Equal(setup.DeveloperInstallationId, setup.FirstTicket.AssignedAgentInstallationId);
        Assert.Equal(WorkTaskStatus.Assigned, setup.FirstTicket.Status);
        Assert.Single(db.AgentPlatformEventOutbox);
        Assert.Equal(setup.DeveloperInstallationId, runtime.LastQueuedInstallationId);
    }

    [Fact]
    public async Task Pulse_WaitsForMergedDependencyThenSelectsDependentTicket()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        setup.FirstSprint.Status = WorkSprintStatus.Active;
        setup.FirstSprint.StartedAt = DateTimeOffset.UtcNow;
        setup.Pipeline.ActiveSprintId = setup.FirstSprint.Id;
        setup.Pipeline.Status = DeliveryPipelineStatuses.Running;
        setup.FirstTicket.Status = WorkTaskStatus.Completed;
        setup.FirstTicket.MergedAt = DateTimeOffset.UtcNow;
        setup.FirstTicket.MergeCommitSha = new string('a', 40);
        setup.SecondTicket.SprintId = setup.FirstSprint.Id;
        db.WorkItemDependencies.Add(new WorkItemDependency
        {
            WorkItemId = setup.SecondTicket.Id,
            DependsOnWorkItemId = setup.FirstTicket.Id
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new RuntimeStub());

        Assert.Equal(1, await service.PulseAsync());

        Assert.Equal(setup.SecondTicket.Id, setup.Pipeline.ActiveWorkItemId);
        Assert.Equal(WorkTaskStatus.Assigned, setup.SecondTicket.Status);
        Assert.Equal(setup.DeveloperInstallationId, setup.SecondTicket.AssignedAgentInstallationId);
    }

    private static WorkDeliveryPipelineService CreateService(
        CSweetDbContext db,
        RuntimeStub runtime) =>
        new(
            db,
            new AuthorizationStub(),
            new SecretStoreStub(),
            runtime,
            new HttpClientFactoryStub());

    private static Setup Seed(CSweetDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var developerInstallationId = Guid.NewGuid();
        var qualityInstallationId = Guid.NewGuid();
        var developmentColumnId = Guid.NewGuid();
        var qualityColumnId = Guid.NewGuid();
        var doneColumnId = Guid.NewGuid();
        var repositoryConnectionId = Guid.NewGuid();
        var firstSprint = new DomainSprint
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            Name = "Sprint 1",
            Goal = "First increment",
            Sequence = 1,
            Status = WorkSprintStatus.Planned,
            CreatedAt = now,
            UpdatedAt = now
        };
        var secondSprint = new DomainSprint
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            Name = "Sprint 2",
            Goal = "Second increment",
            Sequence = 2,
            Status = WorkSprintStatus.Planned,
            CreatedAt = now,
            UpdatedAt = now
        };
        var specification = new WorkItemDeliverySpecification(
            repositoryConnectionId,
            "main",
            ["Implement the behavior."],
            ["The behavior is verified."]);
        var firstTicket = Ticket(
            organizationId, boardId, firstSprint.Id, "First", specification, now, 1024);
        var secondTicket = Ticket(
            organizationId, boardId, secondSprint.Id, "Second", specification, now, 2048);
        var pipeline = new WorkDeliveryPipeline
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            DeveloperInstallationId = developerInstallationId,
            QualityInstallationId = qualityInstallationId,
            DevelopmentColumnId = developmentColumnId,
            QualityColumnId = qualityColumnId,
            DoneColumnId = doneColumnId,
            RepositoryConnectionId = repositoryConnectionId,
            BaseBranch = "main",
            MergeStrategy = "Squash",
            IsEnabled = true,
            Status = DeliveryPipelineStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkBoards.Add(new WorkBoard
        {
            Id = boardId,
            OrganizationId = organizationId,
            Name = "Delivery",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.WorkBoardColumns.AddRange(
            new DomainColumn
            {
                Id = developmentColumnId,
                BoardId = boardId,
                Name = "Development",
                Category = WorkBoardColumnCategory.InProgress
            },
            new DomainColumn
            {
                Id = qualityColumnId,
                BoardId = boardId,
                Name = "QA",
                Category = WorkBoardColumnCategory.InProgress,
                Position = 1
            },
            new DomainColumn
            {
                Id = doneColumnId,
                BoardId = boardId,
                Name = "Done",
                Category = WorkBoardColumnCategory.Done,
                Position = 2
            });
        db.WorkSprints.AddRange(firstSprint, secondSprint);
        db.CoreWorkTasks.AddRange(firstTicket, secondTicket);
        db.WorkDeliveryPipelines.Add(pipeline);
        db.CoreOrganizationUsers.AddRange(
            AgentEmployee(organizationId, developerInstallationId, "Developer", now),
            AgentEmployee(organizationId, qualityInstallationId, "QA", now));
        return new Setup(
            developerInstallationId,
            firstSprint,
            secondSprint,
            firstTicket,
            secondTicket,
            pipeline);
    }

    private static WorkTask Ticket(
        Guid organizationId,
        Guid boardId,
        Guid sprintId,
        string title,
        WorkItemDeliverySpecification specification,
        DateTimeOffset now,
        long rank) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        BoardId = boardId,
        SprintId = sprintId,
        BoardColumnId = Guid.NewGuid(),
        Title = title,
        Kind = WorkItemKind.Story,
        Status = WorkTaskStatus.Ready,
        Priority = WorkTaskPriority.High,
        BoardRank = rank,
        DeliverySpecificationJson = JsonSerializer.Serialize(
            specification,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        CreatedAt = now,
        UpdatedAt = now
    };

    private static OrganizationUser AgentEmployee(
        Guid organizationId,
        Guid installationId,
        string name,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        AgentInstallationId = installationId,
        DisplayName = name,
        EmployeeType = EmployeeType.Agent,
        IsActive = true,
        CreatedAt = now
    };

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Setup(
        Guid DeveloperInstallationId,
        DomainSprint FirstSprint,
        DomainSprint SecondSprint,
        WorkTask FirstTicket,
        WorkTask SecondTicket,
        WorkDeliveryPipeline Pipeline);

    private sealed class AuthorizationStub : IScopedActionAuthorizationService
    {
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(
            Guid organizationId,
            GrantSubjectKind subjectKind,
            Guid subjectId,
            string action,
            GrantScopeKind resourceScopeKind,
            Guid? resourceScopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScopedAuthorizationDecision(true, action));
    }

    private sealed class SecretStoreStub : IPluginSecretStore
    {
        public Task SetAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<string?> GetAsync(Guid installationId, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task RemoveAsync(Guid installationId, string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RuntimeStub : IAgentRuntimeManager
    {
        public Guid? LastQueuedInstallationId { get; private set; }
        public Task<bool> EnsureRuntimeQueuedAsync(Guid installationId, string reason, bool interactive = false, CancellationToken cancellationToken = default)
        {
            LastQueuedInstallationId = installationId;
            return Task.FromResult(true);
        }
        public Task<bool> RestartRuntimeAsync(Guid installationId, string reason, bool interactive = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
