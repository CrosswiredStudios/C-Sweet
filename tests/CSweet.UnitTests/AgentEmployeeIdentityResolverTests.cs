using CSweet.AgentHost.Broker;
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

    private static AgentSession Session(Guid organizationId, Guid installationId) => new(
        Guid.NewGuid().ToString("N"),
        "com.example.agent",
        installationId.ToString("D"),
        organizationId.ToString("D"),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        new AuthorizedAgentGrant(
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            1));
}
