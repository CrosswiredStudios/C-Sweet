using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class BootstrapTeamRosterTests
{
    [Theory]
    [InlineData("active", 1)]
    [InlineData("ended", 0)]
    [InlineData("future", 0)]
    [InlineData("archived", 0)]
    [InlineData("nonmember", 0)]
    [InlineData("foreign", 0)]
    public async Task PortfolioIncludesOnlyCurrentlyAssignedTeamWorkstreams(string scenario, int expected)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organization = Guid.NewGuid(); var installation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var team = Guid.NewGuid(); var stream = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        db.CoreOrganizationUsers.Add(new() { Id = actor, OrganizationId = organization, AgentInstallationId = installation,
            EmployeeType = EmployeeType.Agent, DisplayName = "Producer", IsActive = true });
        db.OrganizationTeams.Add(new() { Id = team, OrganizationId = organization, Name = "Game",
            ArchivedAt = scenario == "archived" ? now : null });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, TeamId = team,
            OrganizationUserId = actor, EndedAt = scenario == "nonmember" ? now : null });
        db.Workstreams.Add(new() { Id = stream, OrganizationId = scenario == "foreign" ? Guid.NewGuid() : organization,
            Name = "Assigned game" });
        db.Workstreams.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, Name = "Unrelated game" });
        db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, TeamId = team,
            WorkstreamId = stream, StartsAt = now.AddDays(scenario == "future" ? 1 : -1),
            EndsAt = scenario == "ended" ? now : null });
        await db.SaveChangesAsync();
        var handler = new WorkstreamGovernanceCapabilityHandler(db, new TestAuditEventWriter(), new AgentEmployeeIdentityResolver(db), TimeProvider.System);
        var session = new AgentSession("session", "producer", installation.ToString(), organization.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { W.WorkstreamCapabilityNames.PortfolioReadV1 }, 1));
        var request = new RequestCapability { RequestId = "portfolio", Capability = W.WorkstreamCapabilityNames.PortfolioReadV1,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new W.ReadPortfolioRequest(), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        var response = Assert.Single(results);
        Assert.True(response.Succeeded, response.Error);
        using var json = JsonDocument.Parse(response.Payload.ToByteArray());
        Assert.Equal(expected, json.RootElement.GetProperty("workstreams").GetArrayLength());
        Assert.Empty(db.WorkstreamSupervisionAssignments);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BootstrapSupervisorCanInspectOnlyTheirActiveLeadsTeam(bool reportsToActor, bool leadActive)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organization = Guid.NewGuid(); var installation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var teamId = Guid.NewGuid(); var leadId = Guid.NewGuid(); var leadInstallation = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new() { Id = actor, OrganizationId = organization, AgentInstallationId = installation,
            EmployeeType = EmployeeType.Agent, DisplayName = "Creative Director" });
        db.CoreOrganizationUsers.Add(new() { Id = leadId, OrganizationId = organization, AgentInstallationId = leadInstallation,
            EmployeeType = EmployeeType.Agent, DisplayName = "Producer", IsActive = leadActive,
            ReportsToOrganizationUserId = reportsToActor ? actor : Guid.NewGuid() });
        db.OrganizationTeams.Add(new() { Id = teamId, OrganizationId = organization, Name = "Game", LeadOrganizationUserId = leadId });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, TeamId = teamId, OrganizationUserId = leadId });
        db.AgentInstallations.Add(new() { Id = leadInstallation, BusinessId = organization.ToString("D"), IsEnabled = true,
            PackageVersion = new() { Id = Guid.NewGuid(), AgentId = "producer", ManifestJson = "{}" },
            RevisionStatus = PluginRevisionStatus.Active,
            Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = leadInstallation,
                ProvidedCapabilitiesJson = "[\"work.execution.run.v1\"]", RequiredCapabilitiesJson = "[\"platform.artifact.read.v1\"]" },
            Schedule = new() { Id = Guid.NewGuid(), AgentInstallationId = leadInstallation, IsEnabled = true } });
        await db.SaveChangesAsync();
        var handler = new WorkstreamGovernanceCapabilityHandler(db, new TestAuditEventWriter(), new AgentEmployeeIdentityResolver(db), TimeProvider.System);
        var session = new AgentSession("session", "creative-director", installation.ToString(), organization.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { W.WorkstreamCapabilityNames.TeamRosterReadV2 }, 1));
        var request = new RequestCapability { RequestId = "roster", Capability = W.WorkstreamCapabilityNames.TeamRosterReadV2,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new W.TeamRosterV2Request(teamId, null, 1, 100), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        var response = Assert.Single(results);
        Assert.Equal(reportsToActor && leadActive, response.Succeeded);
        if (response.Succeeded)
        {
            // The CD is deliberately not a member and no Workstream exists yet.
            Assert.DoesNotContain(db.TeamMemberships, x => x.OrganizationUserId == actor);
            var json = response.Payload.ToStringUtf8();
            Assert.True(json.Contains("work.execution.run.v1"), json);
            Assert.Contains("platform.artifact.read.v1", json);
        }
    }
}
