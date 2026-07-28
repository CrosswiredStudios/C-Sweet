using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class HiringServiceTests
{
    [Fact]
    public async Task RoleBacklog_IsPrioritizedAndScopedToRequestingInstallation()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var firstInstallationId = Guid.NewGuid();
        var secondInstallationId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());

        await service.UpsertRecommendationAsync(organizationId, firstInstallationId,
            new("QA Engineer", "Own release quality", null, [], null, "qa-role") { Priority = 20 });
        await service.UpsertRecommendationAsync(organizationId, firstInstallationId,
            new("Product Manager", "Own product definition", null, [], null, "pm-role") { Priority = 1 });
        await service.UpsertRecommendationAsync(organizationId, secondInstallationId,
            new("Sales Lead", "Own revenue", null, [], null, "sales-role") { Priority = 1 });

        var backlog = await service.ListRecommendationsForInstallationAsync(organizationId, firstInstallationId);

        Assert.Collection(backlog,
            item =>
            {
                Assert.Equal("Product Manager", item.Title);
                Assert.Equal(1, item.Priority);
                Assert.Empty(item.Candidates);
                Assert.Equal(
                    $"/organizations/{organizationId:D}/marketplace?role=Product%20Manager",
                    item.HiringUrl);
            },
            item => { Assert.Equal("QA Engineer", item.Title); Assert.Equal(20, item.Priority); Assert.Empty(item.Candidates); });

        var hired = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            DisplayName = "Product Manager",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            CreatedAt = now
        };
        db.CoreOrganizationUsers.Add(hired);
        await db.SaveChangesAsync();
        var productManager = backlog[0];
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveRecommendationAsync(
            organizationId,
            secondInstallationId,
            new(productManager.Id, hired.Id, "wrong-owner")));
        await service.ResolveRecommendationAsync(
            organizationId,
            firstInstallationId,
            new(productManager.Id, hired.Id, "employee-hired"));
        Assert.DoesNotContain(
            await service.ListRecommendationsForInstallationAsync(organizationId, firstInstallationId),
            x => x.Id == productManager.Id);
    }

    [Fact]
    public async Task CurrentStaffWorkflow_RequiresOwnerAndAssignsApprovedRole()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow; var organizationId = Guid.NewGuid(); var applicationUserId = Guid.NewGuid();
        var installationId = Guid.NewGuid(); var workerId = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Owner", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = now };
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId, WorkerId = workerId,
            DisplayName = "Alex", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = now };
        db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now });
        db.CoreOrganizationUsers.AddRange(owner, employee);
        db.CoreWorkers.Add(new Worker { Id = workerId, OrganizationId = organizationId, Name = "Alex", WorkerType = WorkerType.Human,
            CapabilitiesJson = "[\"operations\"]", IsEnabled = true, CreatedAt = now, UpdatedAt = now });
        var candidate = new WorkforceCandidate { Id = Guid.NewGuid(), OrganizationId = organizationId, Source = "CurrentStaff",
            ExternalCandidateId = workerId.ToString(), DisplayName = "Alex", CapabilitiesJson = "[\"operations\"]",
            Score = .95m, IsHuman = true, IsAvailable = true, ExplanationJson = "{}" };
        db.WorkforceCandidates.Add(candidate);
        await db.SaveChangesAsync();
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());
        var recommendation = await service.UpsertRecommendationAsync(organizationId, installationId,
            new("Operations lead", "Own reliable delivery", null, [$"candidate:{candidate.Id:N}"], $"candidate:{candidate.Id:N}", "rec-1"));
        var workflow = await service.StageWorkflowAsync(organizationId, installationId,
            new(recommendation.Id, recommendation.RecommendedCandidateReference!, "Operations Lead", null, [], "workflow-1"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmWorkflowAsync(organizationId, workflow.Id,
            Guid.NewGuid(), new("not-owner")));
        var approved = await service.ConfirmWorkflowAsync(organizationId, workflow.Id, applicationUserId, new("owner-approval"));

        Assert.Equal("Approved", approved?.Status);
        var updated = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == employee.Id);
        Assert.Equal("Operations Lead", (await db.CoreRoles.SingleAsync(x => x.Id == updated.RoleId)).Name);
    }

    [Fact]
    public async Task EmbeddedAgentWorkflow_PreviewsPinsInstallsAndHiresConfiguredRepository()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var chiefInstallationId = Guid.NewGuid();
        var repositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager";
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Product Company",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = now
        });
        var candidate = new WorkforceCandidate
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Source = "CSweetEmbeddedCatalog",
            ExternalCandidateId = Guid.NewGuid().ToString("D"),
            DisplayName = "Product Manager",
            CapabilitiesJson = "[\"product.strategy\",\"product.discovery\",\"product.roadmap\"]",
            Score = .95m,
            IsAvailable = true,
            ExplanationJson = JsonSerializer.Serialize(new { repositoryUrl })
        };
        db.WorkforceCandidates.Add(candidate);
        await db.SaveChangesAsync();
        var ownerId = await db.CoreOrganizationUsers
            .Where(user => user.OrganizationId == organizationId)
            .Select(user => user.Id)
            .SingleAsync();

        var preview = new RecordingImportPreview(repositoryUrl);
        var installations = new RecordingInstallationService(organizationId);
        var organizationUsers = new RecordingOrganizationUserService();
        var service = new HiringService(
            db,
            organizationUsers,
            new TestAuditEventWriter(),
            preview,
            installations);
        var recommendation = await service.UpsertRecommendationAsync(
            organizationId,
            chiefInstallationId,
            new(
                "Product Manager",
                "Own customer discovery and product outcomes",
                null,
                [$"candidate:{candidate.Id:N}"],
                $"candidate:{candidate.Id:N}",
                "product-manager-first-hire")
            {
                Priority = 1
            });
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageWorkflowAsync(
            organizationId,
            chiefInstallationId,
            new(
                recommendation.Id,
                recommendation.RecommendedCandidateReference!,
                "Product Manager",
                null,
                [],
                "install-product-manager-without-manager")));
        var workflow = await service.StageWorkflowAsync(
            organizationId,
            chiefInstallationId,
            new(
                recommendation.Id,
                recommendation.RecommendedCandidateReference!,
                "Product Manager",
                ownerId,
                [],
                "install-product-manager"));

        var approved = await service.ConfirmWorkflowAsync(
            organizationId,
            workflow.Id,
            applicationUserId,
            new("approve-product-manager"));
        var duplicate = await service.ConfirmWorkflowAsync(
            organizationId,
            workflow.Id,
            applicationUserId,
            new("approve-product-manager-retry"));

        Assert.Equal("Approved", approved?.Status);
        Assert.Equal(approved, duplicate);
        Assert.Equal(2, preview.Requests.Count);
        Assert.Equal(repositoryUrl, preview.Requests[0].RepositoryUrl);
        Assert.Equal(preview.CommitSha, preview.Requests[1].Ref);
        Assert.NotNull(installations.Request);
        Assert.Equal(1, installations.InstallCount);
        Assert.Equal(organizationId.ToString("D"), installations.Request!.BusinessId);
        Assert.Equal(preview.RequestedCapabilities, installations.Request.GrantedRequestedCapabilities);
        Assert.Equal(preview.ProvidedCapabilities, installations.Request.GrantedCapabilities);
        Assert.Equal(installations.InstallationId, organizationUsers.CreatedRequest?.AgentInstallationId);
        Assert.Equal("C-Sweet Product Manager", organizationUsers.CreatedRequest?.DisplayName);
    }

    [Fact]
    public async Task MarketplacePreview_UsesCatalogImportAndSharedConfirmationOrchestrator()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Product Company", CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        var ownerId = await db.CoreOrganizationUsers
            .Where(user => user.OrganizationId == organizationId)
            .Select(user => user.Id)
            .SingleAsync();
        var repositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager";
        var agent = new CSweet.Agent.SDK.AvailableAgent(
            "first-party:product-manager",
            "com.csweet.product-manager",
            CSweet.Agent.SDK.AgentCatalogSource.FirstPartyCatalog,
            [],
            CSweet.Agent.SDK.AgentAvailabilityState.AvailableToInstall,
            null,
            "C-Sweet Product Manager",
            "Own product outcomes.",
            "C-Sweet",
            "Product",
            ["Product Manager"],
            ["product"],
            ["product.strategy"],
            null,
            null,
            null,
            0,
            null,
            repositoryUrl,
            .99m,
            "First-party verified");
        var import = new RecordingImportPreview(repositoryUrl);
        var installations = new RecordingInstallationService(organizationId);
        var organizationUsers = new RecordingOrganizationUserService();
        var service = new HiringService(
            db,
            organizationUsers,
            new TestAuditEventWriter(),
            import,
            installations,
            new RecordingAgentCatalog(agent));
        IAgentHireOrchestrator orchestrator = service;

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new("first-party:product-manager", "Product Manager", null, "marketplace-preview-without-manager")));
        var preview = await orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new("first-party:product-manager", "Product Manager", ownerId, "marketplace-preview"));
        var confirmed = await orchestrator.ConfirmAsync(
            organizationId,
            preview.WorkflowId,
            applicationUserId,
            new("marketplace-confirm")
            {
                ConfigurationSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D")),
                    ["llmModel"] = JsonSerializer.SerializeToElement("test-model")
                }
            });

        Assert.Equal("Pending", preview.Status);
        Assert.Equal("Approved", confirmed?.Status);
        Assert.Equal(Guid.Empty, confirmed?.RecommendationId);
        Assert.Equal(2, import.Requests.Count);
        Assert.Equal(2, preview.ConfigurationFields.Count);
        Assert.Equal(1, installations.InstallCount);
        Assert.Equal("test-model", installations.Request!.ConfigurationSettings["llmModel"].GetString());
        Assert.Equal(installations.InstallationId, organizationUsers.CreatedRequest?.AgentInstallationId);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingImportPreview(string repositoryUrl) : IAgentImportPreviewService
    {
        public string CommitSha { get; } = new('a', 40);
        public IReadOnlyList<string> ProvidedCapabilities { get; } =
            ["assistant.converse.v1", "product-management.plan.v1"];
        public IReadOnlyList<string> RequestedCapabilities { get; } =
            ["platform.llm.chat-stream.v1", "platform.business-profile.read.v1"];
        public List<PreviewAgentImportRequest> Requests { get; } = [];

        public Task<AgentImportPreviewResponse> PreviewAsync(
            PreviewAgentImportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AgentImportPreviewResponse(
                Guid.Parse("7547f772-e46b-4918-a290-f4fba1f04457"),
                repositoryUrl,
                CommitSha,
                new string('b', 64),
                "com.csweet.product-manager",
                "C-Sweet Product Manager",
                "1.0.0",
                "com.csweet",
                "C-Sweet",
                "dotnet-project",
                "src/CSweet.Agents.ProductManager/CSweet.Agents.ProductManager.csproj",
                "net10.0",
                "AlwaysOn",
                ProvidedCapabilities,
                ["com.csweet.agent.onboarded.v1"],
                ["com.csweet.assistant.response.created.v1"],
                [],
                [],
                [],
                "Previewed")
            {
                RequestedCapabilities = RequestedCapabilities,
                ConfigurationFields =
                [
                    new PluginConfigurationField
                    {
                        Key = "llmProviderId", Type = "provider", Label = "LLM provider", Required = true
                    },
                    new PluginConfigurationField
                    {
                        Key = "llmModel", Type = "model", Label = "Model", Required = true
                    }
                ]
            });
        }
    }

    private sealed class RecordingAgentCatalog(CSweet.Agent.SDK.AvailableAgent agent)
        : CSweet.Application.Agents.IAgentCatalogService
    {
        public Task<CSweet.Agent.SDK.AvailableAgentSearchResult> GetAvailableAgentsAsync(
            Guid? organizationId,
            CSweet.Agent.SDK.AvailableAgentSearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CSweet.Agent.SDK.AvailableAgentSearchResult([agent], []));

        public Task<CSweet.Agent.SDK.AvailableAgent?> ResolveAsync(
            Guid? organizationId,
            string agentReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CSweet.Agent.SDK.AvailableAgent?>(
                string.Equals(agent.AgentReference, agentReference, StringComparison.Ordinal) ? agent : null);
    }

    private sealed class RecordingInstallationService(Guid organizationId) : IAgentInstallationService
    {
        public Guid InstallationId { get; } = Guid.NewGuid();
        public InstallAgentRequest? Request { get; private set; }
        public int InstallCount { get; private set; }
        private AgentInstallationResponse? _installed;

        public Task<AgentInstallationResponse> InstallAsync(
            Guid importId,
            InstallAgentRequest request,
            CancellationToken cancellationToken = default)
        {
            InstallCount++;
            Request = request;
            var now = DateTimeOffset.UtcNow;
            _installed = new AgentInstallationResponse(
                InstallationId,
                importId,
                organizationId.ToString("D"),
                "com.csweet.product-manager",
                "C-Sweet Product Manager",
                "1.0.0",
                "C-Sweet",
                new string('a', 40),
                true,
                request.GrantedCapabilities,
                request.GrantedSubscriptions,
                request.GrantedPublications,
                request.GrantedPermissions,
                request.GrantedNetworkAccess,
                request.MemoryMb,
                request.CpuPercent,
                new AgentScheduleResponse(
                    Guid.NewGuid(),
                    request.ActivationMode,
                    request.TickFrequencySeconds,
                    null,
                    null,
                    null,
                    null,
                    request.MaxRuntimeSeconds,
                    0,
                    0,
                    null,
                    request.OverlapPolicy,
                    true),
                now,
                now);
            return Task.FromResult(_installed);
        }

        public Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse?> GetAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(installationId == InstallationId ? _installed : null);
        public Task<AgentInstallationResponse> UpdateScheduleAsync(Guid installationId, UpdateAgentScheduleRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> RunNowAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> RetryBuildAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> DisableAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> UpdateAsync(Guid installationId, UpdateAgentInstallationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> ApproveUpdateAsync(Guid stagedRevisionId, InstallAgentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> EnableAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<RemoveAgentInstallationResponse> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AgentRuntimeRunResponse>> ListRunsAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentBuildLogResponse?> GetBuildLogAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOrganizationUserService : IOrganizationUserService
    {
        public CreateOrganizationUserRequest? CreatedRequest { get; private set; }

        public Task<CoreActionResponse> CreateAsync(
            Guid organizationId,
            CreateOrganizationUserRequest request,
            CancellationToken cancellationToken = default,
            Guid? hiringApplicationUserId = null,
            string hiringSource = "Manual")
        {
            CreatedRequest = request;
            return Task.FromResult(new CoreActionResponse(
                true,
                null,
                "Created",
                OrganizationUser: new OrganizationUserResponse(
                    Guid.NewGuid(),
                    organizationId,
                    request.ReportsToOrganizationUserId,
                    request.RoleId,
                    null,
                    request.DisplayName,
                    null,
                    request.EmployeeType,
                    request.PermissionLevel,
                    DateTimeOffset.UtcNow)
                {
                    AgentInstallationId = request.AgentInstallationId
                }));
        }

        public Task<IReadOnlyList<OrganizationUserResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<OrganizationUserResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreActionResponse> UpdateRoleAsync(Guid organizationId, Guid id, UpdateOrganizationUserRoleRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
