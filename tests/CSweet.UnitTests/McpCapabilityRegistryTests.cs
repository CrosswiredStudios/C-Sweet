using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Communications;
using CSweet.WorkManagement.Contracts;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class McpCapabilityRegistryTests
{
    [Fact]
    public async Task ModelDiscoveryUsesApprovedManifestVisibilityWithoutNameFiltering()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), CommitSha = "abc",
            ManifestDigest = "digest", CapabilityDescriptorsDigest = "descriptors",
            AgentId = "test.agent", AgentName = "Test", Version = "1.0.0",
            PublisherId = "test", PublisherName = "Test", RuntimeType = "dotnet-project",
            ManifestJson = JsonSerializer.Serialize(new
            {
                requires = new object[]
                {
                    new { name = PlatformCapabilities.BusinessProfileRead, scope = "organization", purpose = "Read business.", modelVisible = true },
                    new { name = PlatformCapabilities.OrganizationSnapshotRead, scope = "organization", purpose = "Deterministic reconciliation only.", modelVisible = false }
                }
            })
        };
        var installation = new AgentInstallation
        {
            Id = installationId, InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id,
            PackageVersion = package, BusinessId = organizationId.ToString("D"), IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active
        };
        db.AddRange(package, installation);
        await db.SaveChangesAsync();
        var grants = new HashSet<string>([
            PlatformCapabilities.BusinessProfileRead,
            PlatformCapabilities.OrganizationSnapshotRead
        ], StringComparer.Ordinal);
        var session = new AgentSession("session", "test.agent", installationId.ToString("D"),
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), grants, 1));

        var descriptors = await new McpToolCatalog([]).ListAsync(session, db, CancellationToken.None);

        Assert.True(descriptors.Single(x => x.Capability == PlatformCapabilities.BusinessProfileRead).ModelVisible);
        Assert.False(descriptors.Single(x => x.Capability == PlatformCapabilities.OrganizationSnapshotRead).ModelVisible);
    }

    [Fact]
    public void CoordinationTools_HaveStrictSchemasAndStayHiddenFromModelDiscovery()
    {
        var registry = new McpToolCatalog([]);
        var grants = new HashSet<string>(
            [
                CommunicationCapabilities.CoordinationStart,
                CommunicationCapabilities.CoordinationRespond,
                CommunicationCapabilities.CoordinationRead,
                CommunicationCapabilities.CoordinationCancel
            ], StringComparer.Ordinal);

        var descriptors = registry.List(grants);
        Assert.Equal(4, descriptors.Count);
        Assert.DoesNotContain(descriptors, x => x.ModelVisible);
        foreach (var capability in grants)
        {
            var descriptor = descriptors.SingleOrDefault(x => x.Capability == capability);
            Assert.NotNull(descriptor);
            Assert.False(descriptor!.ModelVisible);
            Assert.False(descriptor.InputSchema.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public void CoordinationMutationSchemas_AcceptTypedArtifacts()
    {
        var registry = new McpToolCatalog([]);
        var artifact = new AgentCoordinationArtifactSubmission(
            "product-management.architecture-brief.v2",
            "2",
            "team:board:brief",
            0,
            true,
            JsonSerializer.SerializeToElement(new { outcome = "Ship a playable increment." }));
        var cases = new (string Capability, object Request)[]
        {
            (
                CommunicationCapabilities.CoordinationStart,
                new StartAgentCoordinationRequest(
                    Guid.NewGuid(), "Delivery planning", "Produce the approved architecture.",
                    ["The design is traceable to product requirements."], "Begin design.",
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "planning-start", artifact)),
            (
                CommunicationCapabilities.CoordinationStartWork,
                new StartWorkItemCoordinationRequest(
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                    "Developer support", "Resolve the blocked implementation stage.",
                    ["Guidance preserves the approved design."], "Please diagnose this blocker.",
                    "support-start", artifact)),
            (
                CommunicationCapabilities.CoordinationRespond,
                new RespondToAgentCoordinationRequest(
                    Guid.NewGuid(), 1, 1, AgentCoordinationDispositions.Continue,
                    "The design proposal is attached.", "planning-response", artifact))
        };

        foreach (var testCase in cases)
        {
            var tool = Assert.Single(registry.List(
                new HashSet<string>([testCase.Capability], StringComparer.Ordinal)));
            JsonSchemaValidator.Validate(
                JsonSerializer.SerializeToElement(
                    testCase.Request,
                    testCase.Request.GetType(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                tool.InputSchema);
        }
    }

    [Fact]
    public void BaselineToolsStillRequireAnExplicitGrant()
    {
        var registry = new McpToolCatalog([]);

        Assert.Empty(registry.List(new HashSet<string>(StringComparer.Ordinal)));
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PlatformCapabilities.UserInputRequest], StringComparer.Ordinal)));

        Assert.Equal("ask_user", tool.Name);
        Assert.Equal(PlatformCapabilities.UserInputRequest, tool.Capability);
    }

    [Fact]
    public void SharedCommunicationCapabilities_AreNotClaimedByTheWorkforceHandler()
    {
        var workforce = new WorkforcePlatformCapabilityHandler(null!, null!, [], []);
        var communications = new CommunicationHubCapabilityHandler(null!, null!);
        IPlatformCapabilityHandler[] handlers = [workforce, communications];

        Assert.False(workforce.CanHandle(PlatformCapabilities.UserInputRequest));
        Assert.False(workforce.CanHandle(PlatformCapabilities.UserActionSuggest));
        Assert.Same(
            communications,
            Assert.Single(handlers, x => x.CanHandle(SuggestedUserActionCapabilities.Suggest)));
    }

    [Fact]
    public void TeamRosterTool_IsReadOnlyAndGrantGated()
    {
        var registry = new McpToolCatalog([]);

        Assert.DoesNotContain(
            registry.List(new HashSet<string>(StringComparer.Ordinal)),
            x => x.Capability == PlatformCapabilities.TeamRosterRead);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PlatformCapabilities.TeamRosterRead], StringComparer.Ordinal)));
        Assert.Equal("read_team_roster", tool.Name);
        Assert.Equal(McpToolExecutionPolicy.ReadOnly, tool.ExecutionPolicy);
    }

    [Fact]
    public void ResourceChangeSchema_AcceptsTheTypedSdkRequestIncludingRoleTeamId()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PlatformCapabilities.ResourceChangePropose], StringComparer.Ordinal)));
        var request = new ResourceChangeProposalRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Validate a browser game prototype.",
            "A compact delivery team is required for the approved proof of concept.",
            1,
            [
                new ResourceChangeRole(
                    "web-game-developer",
                    "Product",
                    "Web Game Developer",
                    "Build the playable prototype.",
                    1,
                    1,
                    "Now",
                    ["web-game-development"],
                    false,
                    null,
                    null)
                {
                    TeamId = null,
                    RoleCategoryKey = "software-developer",
                    PreferredSpecializationKeys = ["game-development"]
                }
            ],
            [],
            [],
            null,
            "resource-change-schema-test");

        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            tool.InputSchema);
    }

    [Fact]
    public void CreateWorkBoardSchema_AcceptsTypedTeamAndKeyRequest()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([CSweet.Contracts.WorkManagement.WorkBoardActions.Create], StringComparer.Ordinal)));
        var request = new CreateWorkBoardRequest(
            "Starfox delivery",
            "The approved team's delivery board.",
            "team-board-create-test")
        {
            TeamId = Guid.NewGuid(),
            Key = "STARFOX"
        };

        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            tool.InputSchema);
    }

    [Fact]
    public void CreateWorkItemSchema_AcceptsTypedProvisionalPlanningRequest()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([WorkItemCapabilities.Create], StringComparer.Ordinal)));
        var request = new CreateWorkItemRequest(
            Guid.NewGuid(),
            "Implement the core driving loop",
            "A provisional story with no dates, estimates, repository, or assignment.",
            WorkItemKinds.Story,
            WorkPriorities.High,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "provisional-driving-loop")
        {
            TypeKey = WorkItemTypeKeys.SoftwareStoryV1,
            Planning = new WorkItemPlanningSpecification(
                ["The player can accelerate, brake, and steer."],
                ["Input changes vehicle motion within one rendered frame."],
                ["Keep the input pipeline deterministic."])
            {
                DependencyItemIds = [Guid.NewGuid()]
            }
        };

        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            tool.InputSchema);
    }

    [Fact]
    public void ListWorkBoardsSchema_AcceptsTypedArrayResponse()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([CSweet.Contracts.WorkManagement.WorkBoardActions.Read], StringComparer.Ordinal)));
        var response = Array.Empty<WorkBoardSummary>();

        Assert.NotNull(tool.OutputSchema);
        Assert.Equal("array", tool.OutputSchema!.Value.GetProperty("type").GetString());
        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            tool.OutputSchema.Value);
    }

    [Fact]
    public void AddPersonalTodoSchema_AcceptsTypedSdkRequestWithNullOptionalMentions()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PersonalTodoCapabilities.Add], StringComparer.Ordinal)));
        var request = new AddPersonalTodoItemRequest(
            "Hire Product Manager",
            "Advance the approved hiring recommendation.",
            WorkPriorities.High,
            null,
            "hiring-recommendation:test",
            CorrelationId: "hiring-recommendation:test")
        {
            StartInBacklog = false
        };

        var payload = JsonSerializer.SerializeToElement(
            request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("mentions").ValueKind);
        JsonSchemaValidator.Validate(payload, tool.InputSchema);
    }

    [Fact]
    public void ReleasePersonalTodoSchema_AcceptsTypedSdkKeepInProgressRequest()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PersonalTodoCapabilities.Release], StringComparer.Ordinal)));
        var request = new ReleasePersonalTodoItemRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            "personal-todo-release-schema-test")
        {
            KeepInProgress = true
        };

        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            tool.InputSchema);
        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(new
            {
                itemId = Guid.NewGuid(),
                eventId = Guid.NewGuid(),
                expectedRevision = 3,
                idempotencyKey = "legacy-personal-todo-release-schema-test"
            }),
            tool.InputSchema);
    }
}
