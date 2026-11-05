using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Theory]
    [InlineData("creative-director", false)]
    [InlineData("game-producer", false)]
    [InlineData("game-producer", true)]
    public async Task SdkApprovedTeamRecommendationPassesSchemaAndRetainsManagerAssignmentAuthorization(string role, bool assign)
    {
        await using var db = CreateDb();
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var team = Guid.NewGuid(); var approval = Guid.NewGuid();
        db.OrganizationTeams.Add(new OrganizationTeam { Id = team, OrganizationId = org, TeamKey = "game-team", Name = "Video Game Team" });
        db.ResourceChangeRequests.Add(new ResourceChangeRequestRecord
        {
            Id = approval, OrganizationId = org, TeamId = team, Status = ResourceChangeRequestStatus.Approved,
            Roles = [new ResourceChangeRoleRecord { Id = Guid.NewGuid(), ResourceChangeRequestId = approval,
                RoleKey = role, TeamId = team, Headcount = 1, IsDesired = true }]
        });
        await db.SaveChangesAsync();
        var service = new HiringService(db, new RecordingOrganizationUserService(), new TestAuditEventWriter());
        var request = new CSweet.Agent.SDK.UpsertHiringRecommendationRequest(
            "Video Game " + role, "Deliver the accepted game direction.", null, [], null, "approved-team:" + role)
        { Priority = 1, RoleKey = role, Headcount = 1, TeamId = team, SourceResourceChangeRequestId = approval, AssignAsProjectManager = assign };
        var json = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(assign, json.GetProperty("assignAsProjectManager").GetBoolean());
        ValidateHiringSchema(json);
        var hostRequest = json.Deserialize<CSweet.Contracts.Core.UpsertHiringRecommendationRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        if (assign)
        {
            // Schema support never grants permission to assign an arbitrary or absent project.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpsertRecommendationAsync(org, installation, hostRequest));
            Assert.Empty(await db.WorkforcePlans.ToListAsync());
        }
        else
        {
            await service.UpsertRecommendationAsync(org, installation, hostRequest);
            var plan = await db.WorkforcePlans.SingleAsync();
            Assert.Equal(role, plan.RoleKey);
            Assert.Equal(team, plan.TeamId);
            Assert.Equal(approval, plan.SourceResourceChangeRequestId);
            Assert.False(plan.AssignAsProjectManager);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiringSchemaKeepsLegacyRequestsAndRejectsInvalidOrUnknownAssignmentFields(bool unknown)
    {
        var request = new JsonObject { ["title"] = "Producer", ["objective"] = "Deliver the game", ["priority"] = 1,
            ["idempotencyKey"] = "legacy" };
        ValidateHiringSchema(JsonSerializer.SerializeToElement(request));
        request[unknown ? "unreviewedAssignment" : "assignAsProjectManager"] = unknown ? JsonValue.Create(true) : JsonValue.Create("false");
        Assert.Throws<InvalidOperationException>(() => ValidateHiringSchema(JsonSerializer.SerializeToElement(request)));
    }

    private static void ValidateHiringSchema(JsonElement payload)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { CSweet.Contracts.Core.HiringCapabilities.UpsertRecommendation }));
        JsonSchemaValidator.Validate(payload, tool.InputSchema);
    }
}
