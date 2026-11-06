using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.WorkManagement.Contracts;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkstreamProposalSchemaTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static WorkstreamPlanProposalV2Request Proposal() => new("Shardfall", "An accepted game", ["Playable"], "Concept", Guid.NewGuid(),
        null, [], [], null, null, null, null, "Deliver approved scope", "initial-proposal", "game", 1,
        JsonSerializer.SerializeToElement(new { genre = "Arcade" }), new(null, null, [], [], [], null),
        [new("prototype", "Prototype", "Concept", null, ["game.build"], ["qa"])],
        [new("artifact", Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "video-game.vision.v1", "Accepted")]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_sdk_serialization_passes_the_effective_broker_schema_for_new_and_imported_projects(bool imported)
    {
        var request = imported ? Proposal() with { ExistingProjectId = Guid.NewGuid(), ExpectedProjectRevision = 3 } : Proposal();
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { WorkstreamCapabilityNames.PlanProposeV2 }));
        var calls = 0;
        var context = new AgentTestRuntime().RegisterCapability<JsonElement, MutationResponse>(WorkstreamCapabilityNames.PlanProposeV2,
            (wire, _) => {
                calls++;
                JsonSchemaValidator.Validate(wire, tool.InputSchema);
                Assert.Equal(imported ? JsonValueKind.String : JsonValueKind.Null, wire.GetProperty("existingProjectId").ValueKind);
                Assert.Equal(imported ? JsonValueKind.Number : JsonValueKind.Null, wire.GetProperty("expectedProjectRevision").ValueKind);
                return Task.FromResult(new MutationResponse(false, 0, Guid.NewGuid(), "Pending"));
            }).CreateContext();
        await context.Platform.ProposeWorkstreamAsync(request);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Schema_property_names_follow_the_serialized_contract_and_unknown_fields_remain_rejected()
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { WorkstreamCapabilityNames.PlanProposeV2 }));
        var payload = JsonSerializer.SerializeToElement(Proposal(), Json);
        Assert.Equal(payload.EnumerateObject().Select(p => p.Name).Order(),
            tool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        var invalid = JsonNode.Parse(payload.GetRawText())!;
        invalid["existingProjectTypo"] = Guid.NewGuid();
        Assert.ThrowsAny<Exception>(() => JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(invalid), tool.InputSchema));
    }

    [Theory]
    [InlineData("existingProjectId", "not-a-uuid")]
    [InlineData("expectedProjectRevision", "wrong-type")]
    [InlineData("name", "")]
    public void Known_field_types_and_existing_business_bounds_stay_enforced(string property, string value)
    {
        var schema = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { WorkstreamCapabilityNames.PlanProposeV2 })).InputSchema;
        var invalid = JsonSerializer.SerializeToNode(Proposal(), Json)!;
        invalid[property] = value;
        Assert.ThrowsAny<Exception>(() => JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(invalid), schema));
    }

    [Theory]
    [InlineData("new", true)]
    [InlineData("import", true)]
    [InlineData("stale", false)]
    [InlineData("foreign", false)]
    [InlineData("identity-only", false)]
    [InlineData("revision-only", false)]
    public async Task Broker_schema_and_real_governance_preserve_adoption_authority_and_idempotent_proposal_creation(string scenario, bool accepted)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var actor = Guid.NewGuid(); var project = Guid.NewGuid();
        var director = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new() { Id = director, OrganizationId = org, AgentInstallationId = Guid.NewGuid(),
            DisplayName = "Director", EmployeeType = EmployeeType.Agent, IsActive = true });
        db.CoreOrganizationUsers.Add(new() { Id = actor, OrganizationId = org, AgentInstallationId = installation,
            DisplayName = "Producer", EmployeeType = EmployeeType.Agent, IsActive = true, ReportsToOrganizationUserId = director });
        db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = "game", Version = 1, Status = "Active", DefinitionDigest = "digest",
            DefinitionJson = "{}", MetadataSchemaJson = "{\"type\":\"object\"}" });
        if (scenario is "import" or "stale" or "foreign")
        {
            var projectOrg = scenario == "foreign" ? Guid.NewGuid() : org;
            db.Workstreams.Add(new() { Id = project, OrganizationId = projectOrg, Name = "Imported", AccountableManagerOrganizationUserId = actor, Revision = 1 });
            db.ProjectRepositoryAssociations.Add(new() { ProjectId = project, OrganizationId = projectOrg, RepositoryId = Guid.NewGuid() });
        }
        await db.SaveChangesAsync();
        var request = Proposal() with { AccountableManagerOrganizationUserId = actor,
            ExistingProjectId = scenario is "import" or "stale" or "foreign" or "identity-only" ? project : null,
            ExpectedProjectRevision = scenario is "import" or "foreign" or "revision-only" ? 1 : scenario == "stale" ? 2 : null };
        var capability = WorkstreamCapabilityNames.PlanProposeV2;
        var schema = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability })).InputSchema;
        var wire = JsonSerializer.SerializeToElement(request, Json);
        JsonSchemaValidator.Validate(wire, schema);
        var session = new AgentSession("session", "producer", installation.ToString(), org.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { capability }, 1));
        var handler = new WorkstreamGovernanceCapabilityHandler(db, new TestAuditEventWriter(), new(db), TimeProvider.System);
        async Task<CapabilityResult> Submit()
        {
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, new RequestCapability { RequestId = Guid.NewGuid().ToString(), Capability = capability,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(request, Json)) }, default)) results.Add(result);
            return Assert.Single(results);
        }
        var first = await Submit();
        Assert.Equal(accepted, first.Succeeded);
        if (accepted)
        {
            var receipt = Assert.Single(db.ActionProposals);
            Assert.True((await Submit()).Succeeded);
            Assert.Equal(receipt.Id, Assert.Single(db.ActionProposals).Id);
            Assert.Contains(db.AgentPlatformEventOutbox, e => e.EventType == "com.csweet.project-approval.requested.v1");
        }
        else Assert.Empty(db.ActionProposals);
    }
}
