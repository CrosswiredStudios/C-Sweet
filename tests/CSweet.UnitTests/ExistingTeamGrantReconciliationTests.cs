using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ExistingTeamGrantReconciliationTests
{
    [Theory]
    [InlineData("approved")]
    [InlineData("unapproved")]
    [InlineData("ended")]
    [InlineData("inactive")]
    [InlineData("disabled")]
    [InlineData("organization-scope")]
    public async Task UpdatedManifestRepairsOnlyApprovedActiveTeamAccessAndPreservesRevocation(string scenario)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var installationId = Guid.NewGuid(); var employeeId = Guid.NewGuid(); var teamId = Guid.NewGuid();
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), ManifestJson = JsonSerializer.Serialize(new
            { requires = new[] { new { name = "work.item.read", scope = scenario == "organization-scope" ? "organization" : "team" } } }) };
        db.AgentInstallations.Add(new() { Id = installationId, BusinessId = org.ToString("D"), PackageVersionId = package.Id,
            PackageVersion = package, Scope = PluginInstallationScope.Organization, IsEnabled = scenario != "disabled",
            RevisionStatus = PluginRevisionStatus.Active, Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = installationId,
                RequiredCapabilitiesJson = scenario == "unapproved" ? "[]" : "[\"work.item.read\"]" } });
        db.CoreOrganizationUsers.Add(new() { Id = employeeId, OrganizationId = org, AgentInstallationId = installationId,
            IsActive = scenario != "inactive", EmployeeType = EmployeeType.Agent });
        db.OrganizationTeams.Add(new() { Id = teamId, OrganizationId = org, LeadOrganizationUserId = Guid.NewGuid() });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, OrganizationUserId = employeeId, TeamId = teamId,
            EndedAt = scenario == "ended" ? DateTimeOffset.UtcNow : null });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var synchronizer = new AgentDefinitionInstallationSynchronizer(db, null!);
        Assert.Equal(scenario == "approved" ? 1 : 0, await synchronizer.SynchronizeAsync());
        db.ChangeTracker.Clear();
        if (scenario != "approved") { Assert.Empty(await db.ScopedActionGrants.ToListAsync()); return; }
        var grant = Assert.Single(await db.ScopedActionGrants.ToListAsync());
        Assert.Equal(teamId, grant.ScopeId); Assert.Equal(installationId, grant.SubjectId); Assert.False(grant.CanDelegate);
        Assert.Equal(0, await synchronizer.SynchronizeAsync());
        grant.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(0, await synchronizer.SynchronizeAsync());
        Assert.NotNull((await db.ScopedActionGrants.SingleAsync()).RevokedAt);
    }
}
