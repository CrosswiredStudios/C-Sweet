using CSweet.AgentHost.Broker;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentEmployeeIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsActiveEmployeeRoleAndManager()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var manager = Employee(organizationId, "Morgan", EmployeeType.Human);
        var role = new Role
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = "Research Director",
            Description = "Own research quality.",
            ResponsibilitiesJson = "[\"Set the research agenda\",\"Review findings\"]",
            AuthorityLevel = AuthorityLevel.ExecutionWithApproval
        };
        var agent = Employee(organizationId, "Avery", EmployeeType.Agent);
        agent.AgentInstallationId = installationId;
        agent.RoleId = role.Id;
        agent.Role = role;
        agent.ReportsToOrganizationUserId = manager.Id;
        db.CoreOrganizationUsers.AddRange(manager, agent);
        await db.SaveChangesAsync();

        var identity = await new AgentEmployeeIdentityResolver(db).ResolveAsync(
            Session(organizationId, installationId));

        Assert.NotNull(identity);
        Assert.Equal(agent.Id.ToString("D"), identity.EmployeeId);
        Assert.Equal("Avery", identity.DisplayName);
        Assert.Equal("Research Director", identity.RoleName);
        Assert.Equal(["Set the research agenda", "Review findings"], identity.RoleResponsibilities);
        Assert.Equal(AuthorityLevel.ExecutionWithApproval.ToString(), identity.AuthorityLevel);
        Assert.Equal(manager.Id.ToString("D"), identity.ManagerEmployeeId);
        Assert.Equal("Morgan", identity.ManagerDisplayName);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotExposeInactiveCrossOrganizationOrUnhiredEmployees()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var inactive = Employee(organizationId, "Inactive", EmployeeType.Agent);
        inactive.AgentInstallationId = installationId;
        inactive.IsActive = false;
        db.CoreOrganizationUsers.Add(inactive);
        await db.SaveChangesAsync();
        var resolver = new AgentEmployeeIdentityResolver(db);

        Assert.Null(await resolver.ResolveAsync(Session(organizationId, installationId)));
        Assert.Null(await resolver.ResolveAsync(Session(Guid.NewGuid(), installationId)));
        Assert.Null(await resolver.ResolveAsync(Session(organizationId, Guid.NewGuid())));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsHiredIdentityWhenRoleAndManagerAreUnassigned()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var agent = Employee(organizationId, "Avery", EmployeeType.Agent);
        agent.AgentInstallationId = installationId;
        db.CoreOrganizationUsers.Add(agent);
        await db.SaveChangesAsync();

        var identity = await new AgentEmployeeIdentityResolver(db).ResolveAsync(
            Session(organizationId, installationId));

        Assert.NotNull(identity);
        Assert.Equal("Avery", identity.DisplayName);
        Assert.True(string.IsNullOrEmpty(identity.RoleName));
        Assert.Empty(identity.RoleResponsibilities);
        Assert.True(string.IsNullOrEmpty(identity.ManagerEmployeeId));
    }

    [Fact]
    public async Task ResolveAsync_WithRosterGrant_InjectsOnlyTheCallersActiveTeam()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var caller = Employee(organizationId, "Dev A", EmployeeType.Agent);
        caller.AgentInstallationId = installationId;
        var qa = Employee(organizationId, "QA", EmployeeType.Human);
        var unrelated = Employee(organizationId, "Other team", EmployeeType.Human);
        var team = new OrganizationTeam
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TeamKey = "delivery-a",
            NormalizedName = "delivery a",
            Name = "Delivery A",
            Description = "Team description is data.",
            LeadOrganizationUserId = qa.Id,
            Revision = 4,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.AddRange(caller, qa, unrelated);
        db.OrganizationTeams.Add(team);
        db.TeamMemberships.AddRange(
            Membership(organizationId, team.Id, caller.Id, caller.Id),
            Membership(organizationId, team.Id, qa.Id, null));
        await db.SaveChangesAsync();

        var resolver = new AgentEmployeeIdentityResolver(db);
        var identity = await resolver.ResolveAsync(
            Session(organizationId, installationId, PlatformCapabilities.TeamRosterRead));

        Assert.NotNull(identity?.TeamContext);
        Assert.Equal("Delivery A", identity.TeamContext.Name);
        Assert.Equal(2, identity.TeamContext.TotalMemberCount);
        Assert.Contains(identity.TeamContext.Members, x => x.EmployeeId == caller.Id.ToString("D") &&
            x.RelationshipToCaller == "Self" && x.AgentInstallationId == installationId);
        Assert.Contains(identity.TeamContext.Members, x => x.EmployeeId == qa.Id.ToString("D") &&
            x.RelationshipToCaller == "TeamLead" && x.AgentInstallationId is null);
        Assert.DoesNotContain(identity.TeamContext.Members, x => x.EmployeeId == unrelated.Id.ToString("D"));
        var serialized = System.Text.Json.JsonSerializer.Serialize(identity.TeamContext);
        Assert.DoesNotContain("Email", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_WithoutRosterGrant_DoesNotRevealTeamContext()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var caller = Employee(organizationId, "Dev", EmployeeType.Agent);
        caller.AgentInstallationId = installationId;
        db.CoreOrganizationUsers.Add(caller);
        await db.SaveChangesAsync();

        var identity = await new AgentEmployeeIdentityResolver(db).ResolveAsync(
            Session(organizationId, installationId));

        Assert.NotNull(identity);
        Assert.Null(identity.TeamContext);
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrganizationUser Employee(Guid organizationId, string name, EmployeeType type) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        DisplayName = name,
        EmployeeType = type,
        CreatedAt = DateTimeOffset.UtcNow,
        IsActive = true
    };

    private static TeamMembership Membership(
        Guid organizationId,
        Guid teamId,
        Guid employeeId,
        Guid? exclusiveAgentEmployeeId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        TeamId = teamId,
        OrganizationUserId = employeeId,
        ExclusiveAgentEmployeeId = exclusiveAgentEmployeeId,
        SourceType = "Test",
        JoinedAt = DateTimeOffset.UtcNow
    };

    private static AgentSession Session(
        Guid organizationId,
        Guid installationId,
        params string[] capabilities) => new(
        Guid.NewGuid().ToString("N"),
        "com.example.agent",
        installationId.ToString("D"),
        organizationId.ToString("D"),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        new AuthorizedAgentGrant(
            new HashSet<string>(),
            new HashSet<string>(),
            capabilities.ToHashSet(StringComparer.Ordinal),
            1));
}
