using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public class CoreServiceTests
{
    #region Organization Tests

    [Fact]
    public async Task OrganizationCreation_RequiresName()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new CoreOrganizationService(dbContext, auditWriter, roleService);

        var request = new CreateOrganizationRequest(
            Name: "  ", // whitespace only
            Industry: null, Mission: null, Stage: null,
            PrimaryGoal: null, ConstraintsJson: null);

        var result = await service.CreateAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
        Assert.Contains("name is required", result.Message!);
    }

    [Fact]
    public async Task OrganizationCreation_SucceedsWithValidName()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new CoreOrganizationService(dbContext, auditWriter, roleService);

        var request = new CreateOrganizationRequest(
            Name: "Acme Corp", Industry: "Technology", Mission: null, Stage: null,
            PrimaryGoal: null, ConstraintsJson: null);

        var result = await service.CreateAsync(request);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Organization);
        Assert.Equal("Acme Corp", result.Organization.Name);
    }

    [Fact]
    public async Task OrganizationCreation_SeesDefaultRoles()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new CoreOrganizationService(dbContext, auditWriter, roleService);

        var request = new CreateOrganizationRequest(
            Name: "Acme Corp", Industry: null, Mission: null, Stage: null,
            PrimaryGoal: null, ConstraintsJson: null);

        var result = await service.CreateAsync(request);

        Assert.True(result.Succeeded);
        var roles = await dbContext.CoreRoles
            .Where(r => r.OrganizationId == result.Organization!.Id)
            .ToListAsync();

        Assert.Equal(5, roles.Count);
        Assert.Contains(roles, r => r.Name == "CEO");
        Assert.Contains(roles, r => r.Name == "Operations");
        Assert.Contains(roles, r => r.Name == "Finance");
        Assert.Contains(roles, r => r.Name == "Marketing");
        Assert.Contains(roles, r => r.Name == "Product");
    }

    [Fact]
    public async Task OrganizationCreation_SeedsOnlyTheCeoEmployee()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new CoreOrganizationService(dbContext, auditWriter, roleService);

        var result = await service.CreateAsync(new CreateOrganizationRequest(
            Name: "Acme Corp", Industry: null, Mission: null, Stage: null,
            PrimaryGoal: null, ConstraintsJson: "{\"personalAssistant\":{\"avatar\":\"strategist\",\"demeanor\":\"Balanced and practical\"}}"));

        Assert.True(result.Succeeded);

        var organizationId = result.Organization!.Id;
        var employees = await dbContext.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync();
        var ceoRole = await dbContext.CoreRoles
            .SingleAsync(x => x.OrganizationId == organizationId && x.Name == "CEO");

        var ceo = Assert.Single(employees);

        Assert.Equal("Owner", ceo.DisplayName);
        Assert.Equal(EmployeeType.Human, ceo.EmployeeType);
        Assert.Equal(OrganizationPermissionLevel.Owner, ceo.PermissionLevel);
        Assert.Equal(ceoRole.Id, ceo.RoleId);
        Assert.Empty(await dbContext.CoreWorkers.Where(x => x.OrganizationId == organizationId).ToListAsync());
    }

    [Fact]
    public async Task OrganizationUpdate_RequiresExistingOrganization()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var roleService = new RoleService(dbContext, auditWriter);
        var service = new CoreOrganizationService(dbContext, auditWriter, roleService);

        var request = new UpdateOrganizationRequest(
            Name: "New Name", Industry: null, Mission: null, Stage: null,
            PrimaryGoal: null, ConstraintsJson: null);

        var result = await service.UpdateAsync(Guid.NewGuid(), request);

        Assert.False(result.Succeeded);
        Assert.Equal("not_found", result.ErrorCode);
    }

    [Fact]
    public void LeadershipAssignmentDeletion_CascadesFromOrganizationUser()
    {
        using var dbContext = CreateDbContext();

        var foreignKey = dbContext.Model
            .FindEntityType(typeof(LeadershipAssignment))!
            .GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(OrganizationUser));

        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    #endregion

    #region Organization User Tests

    [Fact]
    public async Task OrganizationUserCreation_AgentRequiresAnAvailableAgentInstance()
    {
        await using var dbContext = CreateDbContext();
        var service = new OrganizationUserService(dbContext, new TestAuditEventWriter());
        var organization = CreateOrganization();
        dbContext.CoreOrganizations.Add(organization);
        await dbContext.SaveChangesAsync();

        var result = await service.CreateAsync(organization.Id, new CreateOrganizationUserRequest(
            DisplayName: "New Agent",
            Email: null,
            PermissionLevel: 0,
            EmployeeType: (int)EmployeeType.Agent));

        Assert.False(result.Succeeded);
        Assert.Equal("agent_definition_required", result.ErrorCode);
    }

    [Fact]
    public async Task OrganizationUserCreation_AgentRequiresAnActiveManager()
    {
        await using var dbContext = CreateDbContext();
        var service = new OrganizationUserService(dbContext, new TestAuditEventWriter());
        var organization = CreateOrganization();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            AgentId = "example.assistant",
            AgentName = "Assistant",
            Version = "1.0.0",
            Status = AgentPackageVersionStatus.Built,
            ManifestJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            PackageVersion = package,
            BusinessId = organization.Id.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn,
            TickFrequencySeconds = 60,
            MaxRuntimeSeconds = 600,
            OverlapPolicy = OverlapPolicy.Skip,
            IsEnabled = true
        };
        dbContext.AddRange(organization, package, installation);
        await dbContext.SaveChangesAsync();

        var result = await service.CreateAsync(organization.Id, new CreateOrganizationUserRequest(
            "Assistant",
            null,
            (int)OrganizationPermissionLevel.Contributor,
            (int)EmployeeType.Agent,
            AgentInstallationId: installation.Id));

        Assert.False(result.Succeeded);
        Assert.Equal("manager_required", result.ErrorCode);
    }

    [Fact]
    public async Task OrganizationUserCreation_AgentReturnsConversationAndQueuesInitialRuntime()
    {
        await using var dbContext = CreateDbContext();
        var runtimeManager = new RecordingAgentRuntimeManager();
        var service = new OrganizationUserService(
            dbContext,
            new TestAuditEventWriter(),
            agentRuntimeManager: runtimeManager);
        var organization = CreateOrganization();
        var applicationUserId = Guid.NewGuid();
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            AgentId = "example.assistant",
            AgentName = "Assistant",
            Version = "1.0.0",
            Status = AgentPackageVersionStatus.Built,
            ManifestJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = package.Id,
            PackageVersion = package,
            BusinessId = organization.Id.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn,
            TickFrequencySeconds = 60,
            MaxRuntimeSeconds = 600,
            OverlapPolicy = OverlapPolicy.Skip,
            IsEnabled = true
        };
        dbContext.AddRange(organization, owner, package, installation);
        await dbContext.SaveChangesAsync();

        var result = await service.CreateAsync(
            organization.Id,
            new CreateOrganizationUserRequest(
                "Assistant",
                null,
                (int)OrganizationPermissionLevel.Contributor,
                (int)EmployeeType.Agent,
                ReportsToOrganizationUserId: owner.Id,
                AgentInstallationId: installation.Id),
            hiringApplicationUserId: applicationUserId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.OrganizationUser?.InitialConversationId);
        var conversation = await dbContext.CoreConversations.SingleAsync();
        Assert.Equal(conversation.Id, result.OrganizationUser.InitialConversationId);
        Assert.Equal(result.OrganizationUser.Id, conversation.AgentOrganizationUserId);
        var onboardingEvent = await dbContext.AgentOnboardingEventOutbox.SingleAsync();
        Assert.Equal(conversation.Id, onboardingEvent.ConversationId);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Pending, onboardingEvent.Status);
        var hiredEvent = await dbContext.AgentPlatformEventOutbox.SingleAsync();
        Assert.Equal(CSweet.Contracts.Core.HiringEvents.EmployeeHired, hiredEvent.EventType);
        Assert.Equal(organization.Id, hiredEvent.OrganizationId);
        Assert.Contains(result.OrganizationUser.Id.ToString("D"), hiredEvent.DataJson);
        Assert.Equal(installation.Id, runtimeManager.QueuedInstallationId);
        Assert.False(runtimeManager.Interactive);
        Assert.False(runtimeManager.Restarted);
        var personalBoard = await dbContext.WorkBoards.Include(x => x.Columns).SingleAsync(x =>
            x.OwnerOrganizationUserId == result.OrganizationUser.Id);
        Assert.Equal(WorkBoardKind.Personal, personalBoard.Kind);
        Assert.Equal(4, personalBoard.Columns.Count);
        Assert.Equal(PersonalTodoActions.All.Count, await dbContext.ScopedActionGrants.CountAsync(x =>
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == personalBoard.Id &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installation.Id && x.RevokedAt == null &&
            PersonalTodoActions.All.Contains(x.Action)));
        Assert.Equal(5, await dbContext.ScopedActionGrants.CountAsync(x =>
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == personalBoard.Id &&
            x.SubjectKind == GrantSubjectKind.OrganizationUser &&
            x.SubjectId == owner.Id && x.RevokedAt == null &&
            PersonalTodoActions.All.Contains(x.Action)));
    }

    [Fact]
    public async Task OrganizationUserCreation_InheritsDefinitionDefaultsWithoutCopyingThemToOverrides()
    {
        await using var dbContext = CreateDbContext();
        var service = new OrganizationUserService(dbContext, new TestAuditEventWriter());
        var organization = CreateOrganization();
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var providerId = Guid.NewGuid();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            AgentId = "example.chief",
            AgentName = "Chief",
            Version = "1.0.0",
            Status = AgentPackageVersionStatus.Built,
            PackageDigest = $"sha256:{new string('a', 64)}",
            ArtifactSignature = "test-signature",
            ManifestJson = """
                {"configuration":[
                  {"key":"llmProviderId","type":"provider","label":"LLM provider","required":true},
                  {"key":"llmModel","type":"model","label":"Model","required":true}
                ]}
                """,
            ImportedAt = DateTimeOffset.UtcNow
        };
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            PackageSourceId = package.PackageSourceId,
            AgentId = package.AgentId,
            PackageVersionId = package.Id,
            PackageVersion = package,
            Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true,
            DefaultActivationMode = ActivationMode.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = definition.Id,
            SchemaVersion = "1",
            SettingsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                llmProviderId = providerId.ToString("D"),
                llmModel = "local-model"
            }),
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        definition.DefaultRequiredCapabilitiesJson = $"[\"{PlatformCapabilities.LlmChatStream}\"]";
        dbContext.AddRange(
            organization,
            owner,
            package,
            definition,
            new LlmProviderProfile
            {
                Id = providerId,
                Name = "LM Studio",
                ProviderType = LlmProviderType.LmStudio,
                BaseUrl = "http://localhost:1234/v1",
                DefaultChatModel = "local-model",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        await dbContext.SaveChangesAsync();

        var result = await service.CreateAsync(organization.Id, new CreateOrganizationUserRequest(
            "Chief",
            null,
            (int)OrganizationPermissionLevel.Manager,
            (int)EmployeeType.Agent,
            ReportsToOrganizationUserId: owner.Id,
            AgentDefinitionId: definition.Id));

        Assert.True(result.Succeeded, result.Message);
        var configuration = await dbContext.AgentInstallationConfigurations.SingleAsync();
        Assert.Equal("{}", configuration.SettingsJson);
        Assert.NotEqual(definition.Id, result.OrganizationUser!.AgentInstallationId);
        Assert.NotNull(result.OrganizationUser.InitialConversationId);
        var conversation = await dbContext.CoreConversations.SingleAsync();
        Assert.Equal(result.OrganizationUser.InitialConversationId, conversation.Id);
        Assert.Equal(result.OrganizationUser.Id, conversation.AgentOrganizationUserId);
        var onboardingEvent = await dbContext.AgentOnboardingEventOutbox.SingleAsync();
        Assert.Equal(conversation.Id, onboardingEvent.ConversationId);
    }

    [Fact]
    public async Task OrganizationUserDeletion_ArchivesAgentAndConversationsAndUnassignsReports()
    {
        await using var dbContext = CreateDbContext();
        var service = new OrganizationUserService(dbContext, new TestAuditEventWriter());
        var organization = CreateOrganization();
        var manager = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            DisplayName = "Personal Assistant",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Viewer,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var report = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            ReportsToOrganizationUserId = manager.Id,
            DisplayName = "Specialist",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Viewer,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            AgentOrganizationUserId = manager.Id,
            InitiatedByOrganizationUserId = report.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.CoreOrganizations.Add(organization);
        dbContext.CoreOrganizationUsers.AddRange(manager, report);
        dbContext.CoreConversations.Add(conversation);
        await dbContext.SaveChangesAsync();

        var result = await service.DeleteAsync(manager.Id);

        Assert.True(result.Succeeded);
        Assert.Null((await dbContext.CoreOrganizationUsers.SingleAsync(x => x.Id == report.Id)).ReportsToOrganizationUserId);
        var archivedManager = await dbContext.CoreOrganizationUsers.SingleAsync(x => x.Id == manager.Id);
        Assert.False(archivedManager.IsActive);
        Assert.NotNull(archivedManager.ArchivedAt);
        Assert.True(await dbContext.CoreConversations.AnyAsync(x => x.Id == conversation.Id));
    }

    [Theory]
    [InlineData(EmployeeType.Human, OrganizationPermissionLevel.Owner)]
    [InlineData(EmployeeType.Agent, OrganizationPermissionLevel.Viewer)]
    public async Task OrganizationUserDeletion_RejectsApplicationUserMembershipRegardlessOfRole(
        EmployeeType employeeType,
        OrganizationPermissionLevel permissionLevel)
    {
        await using var dbContext = CreateDbContext();
        var service = new OrganizationUserService(dbContext, new TestAuditEventWriter());
        var organization = CreateOrganization();
        var self = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            ApplicationUserId = Guid.NewGuid(),
            OrganizationId = organization.Id,
            DisplayName = "Root Administrator",
            EmployeeType = employeeType,
            PermissionLevel = permissionLevel,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.CoreOrganizations.Add(organization);
        dbContext.CoreOrganizationUsers.Add(self);
        await dbContext.SaveChangesAsync();

        var result = await service.DeleteAsync(self.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("cannot_delete_self", result.ErrorCode);
        Assert.True(await dbContext.CoreOrganizationUsers.AnyAsync(x => x.Id == self.Id));
    }

    #endregion

    #region Role Tests

    [Fact]
    public async Task RoleCreation_RequiresName()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new RoleService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var request = new CreateRoleRequest(
            Name: "  ", Description: "Test", ResponsibilitiesJson: "[]", AuthorityLevel: 0);

        var result = await service.CreateAsync(org.Id, request);

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
    }

    [Fact]
    public async Task RoleCreation_EnforcesUniquenessPerOrganization()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new RoleService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var request = new CreateRoleRequest(
            Name: "CEO", Description: "Test", ResponsibilitiesJson: "[]", AuthorityLevel: 0);

        var result1 = await service.CreateAsync(org.Id, request);
        Assert.True(result1.Succeeded);

        var result2 = await service.CreateAsync(org.Id, request);
        Assert.False(result2.Succeeded);
        Assert.Equal("duplicate_role", result2.ErrorCode);
    }

    [Fact]
    public async Task RoleCreation_AllowsSameNameInDifferentOrganizations()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new RoleService(dbContext, auditWriter);

        var org1 = CreateOrganization();
        var org2 = CreateOrganization();
        dbContext.CoreOrganizations.AddRange(org1, org2);
        await dbContext.SaveChangesAsync();

        var request = new CreateRoleRequest(
            Name: "CEO", Description: "Test", ResponsibilitiesJson: "[]", AuthorityLevel: 0);

        var result1 = await service.CreateAsync(org1.Id, request);
        var result2 = await service.CreateAsync(org2.Id, request);

        Assert.True(result1.Succeeded);
        Assert.True(result2.Succeeded);
    }

    #endregion

    #region Task Tests

    [Fact]
    public async Task TaskCreation_RequiresTitle()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new WorkTaskService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var request = new CreateWorkTaskRequest(
            Title: "  ", Description: string.Empty, StrategicObjectiveId: null,
            AssignedRoleId: null, AssignedWorkerId: null, Status: 0,
            Priority: 0, DueDate: null, RequiresApproval: false);

        var result = await service.CreateAsync(org.Id, request);

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
    }

    [Fact]
    public async Task TaskCreation_ValidatesStrategicObjectiveBelongsToOrganization()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new WorkTaskService(dbContext, auditWriter);

        var org1 = CreateOrganization();
        var org2 = CreateOrganization();
        dbContext.CoreOrganizations.AddRange(org1, org2);
        await dbContext.SaveChangesAsync();

        var objective = new StrategicObjective
        {
            Id = Guid.NewGuid(),
            OrganizationId = org2.Id,
            Title = "Objective",
            Description = "Test",
            Status = ObjectiveStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreStrategicObjectives.Add(objective);
        await dbContext.SaveChangesAsync();

        var request = new CreateWorkTaskRequest(
            Title: "Test Task", Description: string.Empty, StrategicObjectiveId: objective.Id,
            AssignedRoleId: null, AssignedWorkerId: null, Status: 0,
            Priority: 0, DueDate: null, RequiresApproval: false);

        var result = await service.CreateAsync(org1.Id, request);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_objective", result.ErrorCode);
    }

    [Fact]
    public async Task TaskCreation_ValidatesAssignedRoleBelongsToOrganization()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new WorkTaskService(dbContext, auditWriter);

        var org1 = CreateOrganization();
        var org2 = CreateOrganization();
        dbContext.CoreOrganizations.AddRange(org1, org2);
        await dbContext.SaveChangesAsync();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            OrganizationId = org2.Id,
            Name = "CEO",
            Description = "Test",
            ResponsibilitiesJson = "[]",
            AuthorityLevel = AuthorityLevel.Autonomous,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreRoles.Add(role);
        await dbContext.SaveChangesAsync();

        var request = new CreateWorkTaskRequest(
            Title: "Test Task", Description: string.Empty, StrategicObjectiveId: null,
            AssignedRoleId: role.Id, AssignedWorkerId: null, Status: 0,
            Priority: 0, DueDate: null, RequiresApproval: false);

        var result = await service.CreateAsync(org1.Id, request);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_role", result.ErrorCode);
    }

    [Fact]
    public async Task TaskCreation_AllowsGlobalWorkerAssignment()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new WorkTaskService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        dbContext.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            OrganizationId = null, // Global worker
            Name = "Global Worker",
            Description = "Test",
            WorkerType = WorkerType.LocalAgent,
            ExecutionMode = WorkerExecutionMode.InProcess,
            CapabilitiesJson = "[]",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreWorkers.Add(worker);
        await dbContext.SaveChangesAsync();

        var request = new CreateWorkTaskRequest(
            Title: "Test Task", Description: string.Empty, StrategicObjectiveId: null,
            AssignedRoleId: null, AssignedWorkerId: worker.Id, Status: 0,
            Priority: 0, DueDate: null, RequiresApproval: false);

        var result = await service.CreateAsync(org.Id, request);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region Approval Tests

    [Fact]
    public async Task ArtifactApproval_StateTransitionWorks()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new ArtifactApprovalService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Type = ArtifactType.Document,
            Title = "Test Document",
            Content = "Test content",
            Version = 1,
            ApprovalStatus = ApprovalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreArtifacts.Add(artifact);
        await dbContext.SaveChangesAsync();

        var result = await service.ApproveAsync(artifact.Id, "Looks good");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Approval);
        Assert.Equal((int)ApprovalStatus.Approved, result.Approval.Status);

        var updated = await dbContext.CoreArtifacts.FindAsync(artifact.Id);
        Assert.Equal(ApprovalStatus.Approved, updated!.ApprovalStatus);
    }

    [Fact]
    public async Task ArtifactApproval_RejectionWorks()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new ArtifactApprovalService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Type = ArtifactType.Document,
            Title = "Test Document",
            Content = "Test content",
            Version = 1,
            ApprovalStatus = ApprovalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreArtifacts.Add(artifact);
        await dbContext.SaveChangesAsync();

        var result = await service.RejectAsync(artifact.Id, "Needs revision");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Approval);
        Assert.Equal((int)ApprovalStatus.Rejected, result.Approval.Status);
    }

    [Fact]
    public async Task ArtifactApproval_CannotReApproveAlreadyApprovedArtifact()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new ArtifactApprovalService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Type = ArtifactType.Document,
            Title = "Test Document",
            Content = "Test content",
            Version = 1,
            ApprovalStatus = ApprovalStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreArtifacts.Add(artifact);
        await dbContext.SaveChangesAsync();

        var result = await service.ApproveAsync(artifact.Id, "Already approved");

        Assert.False(result.Succeeded);
        Assert.Equal("approval_conflict", result.ErrorCode);
    }

    [Fact]
    public async Task ArtifactApproval_RequiresArtifactToExist()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new ArtifactApprovalService(dbContext, auditWriter);

        var result = await service.ApproveAsync(Guid.NewGuid(), "Test");

        Assert.False(result.Succeeded);
        Assert.Equal("not_found", result.ErrorCode);
    }

    [Fact]
    public async Task ArtifactApproval_RequestRevisionWorks()
    {
        await using var dbContext = CreateDbContext();
        var auditWriter = new TestAuditEventWriter();
        var service = new ArtifactApprovalService(dbContext, auditWriter);

        var org = CreateOrganization();
        dbContext.CoreOrganizations.Add(org);
        await dbContext.SaveChangesAsync();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Type = ArtifactType.Document,
            Title = "Test Document",
            Content = "Test content",
            Version = 1,
            ApprovalStatus = ApprovalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreArtifacts.Add(artifact);
        await dbContext.SaveChangesAsync();

        var result = await service.RequestRevisionAsync(artifact.Id, "Please revise section 3");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Approval);
        Assert.Equal((int)ApprovalStatus.RevisionRequested, result.Approval.Status);
    }

    #endregion

    #region Helpers

    private static DbContextOptions<CSweetDbContext> CreateDbContextOptions()
    {
        return new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
    }

    private static CSweetDbContext CreateDbContext()
    {
        return new CSweetDbContext(CreateDbContextOptions());
    }

    private static Organization CreateOrganization()
    {
        return new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Test Org",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class RecordingAgentRuntimeManager : IAgentRuntimeManager
    {
        public Guid? QueuedInstallationId { get; private set; }
        public bool Interactive { get; private set; }
        public bool Restarted { get; private set; }

        public Task<bool> EnsureRuntimeQueuedAsync(
            Guid installationId,
            string reason,
            bool interactive = false,
            CancellationToken cancellationToken = default)
        {
            QueuedInstallationId = installationId;
            Interactive = interactive;
            Restarted = false;
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
            Restarted = true;
            return Task.FromResult(true);
        }

        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    #endregion
}

public class TestAuditEventWriter : IAuditEventWriter
{
    public List<AuditEventWriteRequest> Events { get; } = [];

    public Task WriteAsync(string eventType, string entityTypeName, Guid? entityId = null, string? summary = null, string? metadataJson = null, CancellationToken cancellationToken = default)
    {
        Events.Add(new AuditEventWriteRequest(
            eventType,
            EntityType: entityTypeName,
            EntityId: entityId,
            Summary: summary,
            MetadataJson: metadataJson));
        return Task.CompletedTask;
    }

    public Task<Guid> AppendAsync(
        AuditEventWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        Events.Add(request);
        return Task.FromResult(Guid.NewGuid());
    }
}
