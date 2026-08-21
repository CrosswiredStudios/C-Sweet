using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.BusinessOnboarding;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public class BusinessOnboardingServiceTests
{
    [Fact]
    public async Task DurableOperation_ContinuesAfterHandoffAndCreatesOneBusiness()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var definitionService = new AgentDefinitionService(
            dbContext, auditWriter, new NoOpAgentBuildService());
        var service = new BusinessOnboardingService(
            new CoreOrganizationService(dbContext, auditWriter, roleService),
            roleService,
            new StrategicObjectiveService(dbContext, auditWriter),
            new WorkerService(dbContext, auditWriter),
            auditWriter,
            new ExecutiveBriefingService(dbContext, auditWriter, TimeProvider.System),
            dbContext,
            agentDefinitions: definitionService);
        var applicationUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            DisplayName = "Durable Owner",
            UserName = "durable@example.com",
            NormalizedUserName = "DURABLE@EXAMPLE.COM",
            Email = "durable@example.com",
            NormalizedEmail = "DURABLE@EXAMPLE.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            AgentId = "example.durable-chief",
            AgentName = "Durable Chief",
            Version = "1.0.0",
            PluginKind = PluginKind.Agent,
            ManifestJson = """{"kind":"agent","provides":[{"name":"assistant.converse.v1"},{"name":"assistant.plan-work.v1"},{"name":"management.check-in.v1"}]}""",
            Status = AgentPackageVersionStatus.Built,
            PackageDigest = new string('f', 64),
            ArtifactSignature = "test-signature",
            ImportedAt = DateTimeOffset.UtcNow
        };
        dbContext.Users.Add(applicationUser);
        dbContext.AgentPackageVersions.Add(package);
        await dbContext.SaveChangesAsync();
        var installRequest = new CSweet.Contracts.Agents.InstallAgentRequest(
            "default", "OnDemand", 3600, "Skip",
            ["assistant.converse.v1", "assistant.plan-work.v1", "management.check-in.v1"],
            [], [], [], [], 600, 512, 50);
        var request = new StartBusinessOnboardingRequest(
            "Async Example Co", "Software", "Build asynchronously.", package.Id, "Avery",
            "durable-business-onboarding", installRequest);

        var started = await service.StartAsync(request, applicationUser.Id);
        var replayed = await service.StartAsync(request, applicationUser.Id);

        Assert.Equal(BusinessOnboardingOperationStatuses.Starting, started.Status);
        Assert.Equal(started.Id, replayed.Id);
        Assert.True(await service.ProcessNextAsync("test-worker"));

        var completed = await service.GetForUserAsync(started.Id, applicationUser.Id);
        Assert.NotNull(completed);
        Assert.Equal(BusinessOnboardingOperationStatuses.Succeeded, completed.Status);
        Assert.NotNull(completed.OrganizationId);
        Assert.Single(await dbContext.CoreOrganizations.ToListAsync());
        Assert.Single(await dbContext.BusinessOnboardingOperations.ToListAsync());
        Assert.Single(await dbContext.CoreOrganizationUsers
            .Where(x => x.EmployeeType == EmployeeType.Agent).ToListAsync());

        await service.DismissAsync(started.Id, applicationUser.Id);
        Assert.Empty(await service.ListForUserAsync(applicationUser.Id));
    }

    [Fact]
    public async Task CompleteAsync_AssignsAnyEnabledAgentAsChiefAndActivatesOrganizationWithWarnings()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var runtimeManager = new RecordingAgentRuntimeManager();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new BusinessOnboardingService(
            new CoreOrganizationService(dbContext, auditWriter, roleService),
            roleService,
            new StrategicObjectiveService(dbContext, auditWriter),
            new WorkerService(dbContext, auditWriter),
            auditWriter,
            new ExecutiveBriefingService(dbContext, auditWriter, TimeProvider.System),
            dbContext,
            agentRuntimeManager: runtimeManager);
        var applicationUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            DisplayName = "Morgan Owner",
            UserName = "owner@example.com",
            NormalizedUserName = "OWNER@EXAMPLE.COM",
            Email = "owner@example.com",
            NormalizedEmail = "OWNER@EXAMPLE.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            AgentId = "example.arbitrary-agent",
            AgentName = "Arbitrary Agent",
            Version = "1.0.0",
            PluginKind = PluginKind.Agent,
            ManifestJson = """{"kind":"agent","provides":[{"name":"assistant.converse.v1"}]}""",
            Status = AgentPackageVersionStatus.Built,
            PackageDigest = new string('d', 64),
            ArtifactSignature = "test-signature",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var definition = CreateDefinition(package, ActivationMode.AlwaysOn);
        dbContext.Users.Add(applicationUser);
        dbContext.AgentDefinitions.Add(definition);
        await dbContext.SaveChangesAsync();

        var result = await service.CompleteAsync(new CompleteBusinessOnboardingRequest(
            "Example Co", "Software", "Help teams make better operating decisions.", definition.Id, "Avery"),
            applicationUserId: applicationUser.Id);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Onboarding);
        Assert.True(result.Onboarding.OrganizationActivated);
        Assert.NotNull(result.Onboarding.ChiefOrganizationUserId);
        Assert.Equal(6, result.Onboarding.CreatedRoleCount);
        Assert.Equal(2, result.Onboarding.ChiefReadinessWarnings.Count);

        var organization = await dbContext.CoreOrganizations.SingleAsync(x => x.Id == result.Onboarding.OrganizationId);
        var chief = await dbContext.CoreOrganizationUsers.SingleAsync(x => x.Id == result.Onboarding.ChiefOrganizationUserId);
        var installation = await dbContext.AgentInstallations.SingleAsync(x => x.Id == chief.AgentInstallationId);
        var ceo = await dbContext.CoreOrganizationUsers.SingleAsync(x => x.Id == chief.ReportsToOrganizationUserId);
        var leadership = await dbContext.LeadershipAssignments.SingleAsync(x => x.OrganizationUserId == chief.Id);
        Assert.Equal(OrganizationStatus.Active, organization.Status);
        Assert.Equal(EmployeeType.Agent, chief.EmployeeType);
        Assert.Equal("Avery", chief.DisplayName);
        Assert.Equal(applicationUser.Id, ceo.ApplicationUserId);
        Assert.Equal("chief-of-staff", leadership.PositionKey);
        Assert.Equal(organization.Id.ToString("D"), installation.BusinessId);
        Assert.Equal(installation.Id, runtimeManager.QueuedInstallationId);
        Assert.False(runtimeManager.Interactive);
        Assert.Equal(1, runtimeManager.ReconcileCount);
    }

    [Fact]
    public async Task CompleteAsync_CreatesOrganizationDefaultsObjectiveEmptyBoardAndWorker()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var organizationService = new CoreOrganizationService(dbContext, auditWriter, roleService);
        var objectiveService = new StrategicObjectiveService(dbContext, auditWriter);
        var workerService = new WorkerService(dbContext, auditWriter);
        var service = new BusinessOnboardingService(
            organizationService,
            roleService,
            objectiveService,
            workerService,
            auditWriter,
            new ExecutiveBriefingService(dbContext, auditWriter, TimeProvider.System),
            dbContext);
        var applicationUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            DisplayName = "Alex Admin",
            UserName = "admin@example.com",
            NormalizedUserName = "ADMIN@EXAMPLE.COM",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            EmailConfirmed = true,
            IsInitialAdministrator = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Users.Add(applicationUser);
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), AgentId = "example.chief", AgentName = "Example Chief",
            Version = "1.0.0", PluginKind = PluginKind.Agent,
            ManifestJson = """{"kind":"agent","provides":[{"name":"assistant.converse.v1"},{"name":"assistant.plan-work.v1"},{"name":"management.check-in.v1"},{"name":"agent.configuration.describe.v1"}]}""",
            Status = AgentPackageVersionStatus.Built,
            PackageDigest = new string('e', 64),
            ArtifactSignature = "test-signature",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var definition = CreateDefinition(package, ActivationMode.OnDemand);
        dbContext.AgentDefinitions.Add(definition);
        await dbContext.SaveChangesAsync();

        var result = await service.CompleteAsync(new CompleteBusinessOnboardingRequest(
            "Example Co",
            "Software",
            "Launch a paid MVP that makes planning easier for small teams.",
            definition.Id), applicationUserId: applicationUser.Id);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Onboarding);
        Assert.Equal(6, result.Onboarding.CreatedRoleCount);
        Assert.Equal(0, result.Onboarding.CreatedTaskCount);
        Assert.True(result.Onboarding.OrganizationActivated);
        var chiefConversation = await dbContext.CoreConversations.SingleAsync(x =>
            x.OrganizationId == result.Onboarding.OrganizationId &&
            x.AgentOrganizationUserId == result.Onboarding.ChiefOrganizationUserId);
        Assert.Equal(
            $"/organizations/{result.Onboarding.OrganizationId}/communications/{chiefConversation.Id:D}",
            result.Onboarding.NextRoute);

        var organizationId = result.Onboarding.OrganizationId;
        var organization = await dbContext.CoreOrganizations.SingleAsync(x => x.Id == organizationId);
        var roles = await dbContext.CoreRoles.Where(x => x.OrganizationId == organizationId).ToListAsync();
        var employees = await dbContext.CoreOrganizationUsers.Where(x => x.OrganizationId == organizationId).ToListAsync();
        var objective = await dbContext.CoreStrategicObjectives.SingleAsync(x => x.OrganizationId == organizationId);
        var tasks = await dbContext.CoreWorkTasks.Where(x => x.OrganizationId == organizationId).ToListAsync();
        var board = await dbContext.WorkBoards
            .Include(x => x.Columns)
            .SingleAsync(x => x.OrganizationId == organizationId);
        var worker = await dbContext.CoreWorkers.SingleAsync(x => x.Id == result.Onboarding.DefaultWorkerId);

        Assert.Equal("Example Co", organization.Name);
        Assert.Equal("Software", organization.Industry);
        Assert.Null(organization.Stage);
        Assert.Null(organization.PrimaryGoal);
        Assert.Equal("Launch a paid MVP that makes planning easier for small teams.", organization.Mission);
        Assert.Equal(OrganizationStatus.Active, organization.Status);
        Assert.Null(organization.ConstraintsJson);
        Assert.Contains(roles, x => x.Name == "CEO" && x.AuthorityLevel == AuthorityLevel.ExecutionWithApproval);
        var self = Assert.Single(employees, x => x.EmployeeType == EmployeeType.Human);
        var chief = Assert.Single(employees, x => x.EmployeeType == EmployeeType.Agent);
        var installation = await dbContext.AgentInstallations.SingleAsync(x => x.Id == chief.AgentInstallationId);
        Assert.Equal("Alex Admin", self.DisplayName);
        Assert.Equal(applicationUser.Id, self.ApplicationUserId);
        Assert.Equal("admin@example.com", self.Email);
        Assert.Equal(EmployeeType.Human, self.EmployeeType);
        Assert.Equal(OrganizationPermissionLevel.Owner, self.PermissionLevel);
        Assert.Equal("CEO", roles.Single(x => x.Id == self.RoleId).Name);
        Assert.Equal("Chief of Staff", roles.Single(x => x.Id == chief.RoleId).Name);
        Assert.Contains(roles, x => x.Name == "Marketing" && x.ResponsibilitiesJson.Contains("Define target customer"));
        Assert.Equal(ObjectiveStatus.Active, objective.Status);
        Assert.Equal("Launch a paid MVP that makes planning easier for small teams.", objective.Title);
        Assert.Empty(tasks);
        Assert.True(board.IsDefault);
        Assert.Equal("Company work", board.Name);
        Assert.Collection(
            board.Columns.OrderBy(x => x.Position),
            column => Assert.Equal("To Do", column.Name),
            column => Assert.Equal("Done", column.Name));
        Assert.Equal("Local Strategy Agent", worker.Name);
        Assert.Equal(WorkerType.LocalAgent, worker.WorkerType);
        Assert.True(worker.RequiresHumanApproval);

        var operationsRole = roles.Single(x => x.Name == "Operations");
        var userService = new OrganizationUserService(dbContext, auditWriter);
        var roleUpdate = await userService.UpdateRoleAsync(
            organizationId,
            self.Id,
            new UpdateOrganizationUserRoleRequest(operationsRole.Id));
        Assert.True(roleUpdate.Succeeded);
        var updatedSelf = await dbContext.CoreOrganizationUsers.SingleAsync(x => x.Id == self.Id);
        Assert.Equal(operationsRole.Id, updatedSelf.RoleId);
        Assert.Equal(OrganizationPermissionLevel.Owner, updatedSelf.PermissionLevel);

        Assert.Equal(organization.Id.ToString("D"), installation.BusinessId);
    }

    [Fact]
    public async Task CompleteAsync_RequiresBusinessNameAndChiefAgent()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new BusinessOnboardingService(
            new CoreOrganizationService(dbContext, auditWriter, roleService),
            roleService,
            new StrategicObjectiveService(dbContext, auditWriter),
            new WorkerService(dbContext, auditWriter),
            auditWriter,
            new ExecutiveBriefingService(dbContext, auditWriter, TimeProvider.System),
            dbContext);

        var missingName = await service.CompleteAsync(new CompleteBusinessOnboardingRequest(
            " ",
            null,
            "Launch",
            Guid.Empty));
        var missingChief = await service.CompleteAsync(new CompleteBusinessOnboardingRequest(
            "Example Co",
            null,
            "Launch",
            Guid.Empty));

        Assert.False(missingName.Succeeded);
        Assert.Equal("validation_error", missingName.ErrorCode);
        Assert.False(missingChief.Succeeded);
        Assert.Equal("chief_agent_required", missingChief.ErrorCode);
    }

    private static AgentDefinition CreateDefinition(AgentPackageVersion package, ActivationMode activationMode)
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            PackageSourceId = package.PackageSourceId,
            AgentId = package.AgentId,
            PackageVersionId = package.Id,
            PackageVersion = package,
            Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true,
            DefaultActivationMode = activationMode,
            CreatedAt = now,
            UpdatedAt = now
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = definition.Id,
            SchemaVersion = "1",
            SettingsJson = "{}",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        return definition;
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CSweetDbContext(options);
    }

    private sealed class RecordingAgentRuntimeManager : IAgentRuntimeManager
    {
        public Guid? QueuedInstallationId { get; private set; }
        public bool Interactive { get; private set; }
        public int ReconcileCount { get; private set; }

        public Task<bool> EnsureRuntimeQueuedAsync(
            Guid installationId,
            string reason,
            bool interactive = false,
            CancellationToken cancellationToken = default)
        {
            QueuedInstallationId = installationId;
            Interactive = interactive;
            return Task.FromResult(true);
        }

        public Task<bool> RestartRuntimeAsync(
            Guid installationId,
            string reason,
            bool interactive = false,
            CancellationToken cancellationToken = default)
        {
            QueuedInstallationId = installationId;
            Interactive = interactive;
            return Task.FromResult(true);
        }

        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
        {
            ReconcileCount++;
            return Task.FromResult(0);
        }
    }

    private sealed class NoOpAgentBuildService : IAgentBuildService
    {
        public Task<Guid> QueueAsync(Guid packageVersionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
