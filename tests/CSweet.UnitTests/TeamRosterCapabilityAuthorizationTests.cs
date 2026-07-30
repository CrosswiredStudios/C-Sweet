using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class TeamRosterCapabilityAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RosterRead_DeniesMissingGrantOrNonMember(bool grantCapability)
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Example",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentInstallationId = installationId,
            DisplayName = "Unassigned agent",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var handler = new WorkforcePlatformCapabilityHandler(
            db,
            new TestAuditEventWriter(),
            [],
            [],
            identityResolver: new AgentEmployeeIdentityResolver(db));
        var grants = grantCapability
            ? new HashSet<string>([PlatformCapabilities.TeamRosterRead], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"),
            "com.example.agent",
            installationId.ToString("D"),
            organizationId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                grants,
                1));
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = PlatformCapabilities.TeamRosterRead,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new TeamRosterRequest()))
        };

        var results = new List<CapabilityResult>();
        await foreach (var item in handler.HandleAsync(session, request, CancellationToken.None))
            results.Add(item);

        var response = Assert.Single(results);
        Assert.False(response.Succeeded);
        Assert.Contains(
            grantCapability ? "not an active member" : "not granted",
            response.Error,
            StringComparison.OrdinalIgnoreCase);
    }
}
