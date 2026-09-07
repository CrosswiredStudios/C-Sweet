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
