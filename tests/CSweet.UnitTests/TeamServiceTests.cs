using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class TeamServiceTests
{
    [Fact]
    public async Task HumansMayJoinSeveralTeams_WhileAgentLifetimeAssignmentIsExclusive()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var first = (await service.CreateAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            new CreateTeamRequest("Alpha", "First delivery team", setup.Manager.Id))).Team;
        var second = (await service.CreateAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            new CreateTeamRequest("Beta", "Second delivery team", setup.Manager.Id))).Team;

        await service.UpsertMemberAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id, setup.Human.Id,
            new UpsertTeamMembershipRequest(null, first.Revision));
        first = (await service.GetAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id))!.Team;
        await service.UpsertMemberAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, second.Id, setup.Human.Id,
            new UpsertTeamMembershipRequest(null, second.Revision));

        first = (await service.GetAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id))!.Team;
        await service.UpsertMemberAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id, setup.Agent.Id,
            new UpsertTeamMembershipRequest(null, first.Revision));
        first = (await service.GetAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id))!.Team;
        await service.RemoveMemberAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id, setup.Agent.Id,
            new TeamRevisionRequest(first.Revision));
        second = (await service.GetAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, second.Id))!.Team;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertMemberAsync(
                setup.OrganizationId, setup.ManagerApplicationUserId, second.Id, setup.Agent.Id,
                new UpsertTeamMembershipRequest(null, second.Revision)));
        Assert.Contains("new agent installation", error.Message, StringComparison.OrdinalIgnoreCase);

        first = (await service.GetAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id))!.Team;
        var reactivated = await service.UpsertMemberAsync(
            setup.OrganizationId, setup.ManagerApplicationUserId, first.Id, setup.Agent.Id,
            new UpsertTeamMembershipRequest(null, first.Revision));
        Assert.Contains(reactivated.Team.Members, x =>
            x.OrganizationUserId == setup.Agent.Id && x.EndedAt is null);
        Assert.Equal(2, await db.TeamMemberships.CountAsync(x => x.OrganizationUserId == setup.Human.Id));
        Assert.Single(await db.TeamMemberships.Where(x => x.OrganizationUserId == setup.Agent.Id).ToListAsync());
    }

    [Fact]
    public async Task ArchiveRevokesTeamGrants_AndRestoreDoesNotReviveThem()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var team = (await service.CreateAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            new CreateTeamRequest("Delivery", null, setup.Manager.Id))).Team;
        var grant = new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = Guid.NewGuid(),
            Action = "work.board.create",
            ScopeKind = GrantScopeKind.Team,
            ScopeId = team.Id,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedAt = DateTimeOffset.UtcNow
        };
        db.ScopedActionGrants.Add(grant);
        await db.SaveChangesAsync();

        var archived = await service.ArchiveAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            team.Id,
            new TeamRevisionRequest(team.Revision));
        Assert.True(archived.Team.IsArchived);
        Assert.NotNull((await db.ScopedActionGrants.SingleAsync()).RevokedAt);

        var restored = await service.RestoreAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            team.Id,
            new TeamRevisionRequest(archived.Team.Revision));
        Assert.False(restored.Team.IsArchived);
        Assert.NotNull((await db.ScopedActionGrants.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task MembershipRemovalRevokesGeneratedGrantButPreservesManualGrant()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        var now = DateTimeOffset.UtcNow;
        var installationId = setup.Agent.AgentInstallationId!.Value;
        var packageVersionId = Guid.NewGuid();
        db.AgentPackageVersions.Add(new AgentPackageVersion
        {
            Id = packageVersionId,
            PackageSourceId = Guid.NewGuid(),
            ManifestJson = """
                { "requires": [{ "name": "work.board.read", "scope": "team" }] }
                """,
            AgentId = "com.example.team-agent",
            AgentName = "Team agent",
            Version = "1.0.0",
            ImportedAt = now
        });
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId,
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = packageVersionId,
            BusinessId = setup.OrganizationId.ToString("D"),
            Scope = PluginInstallationScope.Organization,
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.AgentInstallationGrants.Add(new AgentInstallationGrant
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installationId,
            RequiredCapabilitiesJson = "[\"work.board.read\"]",
            ApprovedAt = now
        });
        await db.SaveChangesAsync();

        var service = Service(db);
        var team = (await service.CreateAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            new CreateTeamRequest("Delivery", null, setup.Manager.Id))).Team;
        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installationId,
            Action = "manual.team.action",
            ScopeKind = GrantScopeKind.Team,
            ScopeId = team.Id,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedBySubjectId = setup.Manager.Id,
            GrantedAt = now
        });
        await db.SaveChangesAsync();

        team = (await service.UpsertMemberAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            team.Id,
            setup.Agent.Id,
            new UpsertTeamMembershipRequest(null, team.Revision))).Team;
        var active = await db.ScopedActionGrants.Where(x => x.RevokedAt == null).ToListAsync();
        Assert.Contains(active, x => x.Action == "work.board.read");
        Assert.Contains(active, x => x.Action == "manual.team.action");

        await service.RemoveMemberAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            team.Id,
            setup.Agent.Id,
            new TeamRevisionRequest(team.Revision));

        var grants = await db.ScopedActionGrants.ToListAsync();
        Assert.NotNull(grants.Single(x => x.Action == "work.board.read").RevokedAt);
        Assert.Null(grants.Single(x => x.Action == "manual.team.action").RevokedAt);
    }

    [Fact]
    public async Task ContributorAndCrossOrganizationActorsCannotManageTeams()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        var other = Employee(Guid.NewGuid(), "Other", EmployeeType.Human);
        other.ApplicationUserId = Guid.NewGuid();
        other.PermissionLevel = OrganizationPermissionLevel.Owner;
        db.CoreOrganizationUsers.Add(other);
        await db.SaveChangesAsync();
        var service = Service(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(
                setup.OrganizationId,
                setup.Contributor.ApplicationUserId!.Value,
                new CreateTeamRequest("Denied", null, setup.Manager.Id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(
                setup.OrganizationId,
                other.ApplicationUserId!.Value,
                new CreateTeamRequest("Cross tenant", null, setup.Manager.Id)));
    }

    [Fact]
    public async Task CurrentLeadCannotBeRemoved()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var team = (await service.CreateAsync(
            setup.OrganizationId,
            setup.ManagerApplicationUserId,
            new CreateTeamRequest("Delivery", null, setup.Manager.Id))).Team;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveMemberAsync(
                setup.OrganizationId,
                setup.ManagerApplicationUserId,
                team.Id,
                setup.Manager.Id,
                new TeamRevisionRequest(team.Revision)));

        Assert.Contains("another team lead", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(team.Members, x =>
            x.OrganizationUserId == setup.Manager.Id &&
            x.IsLead &&
            x.EndedAt is null);
    }

    [Fact]
    public void MembershipModelHasConcurrencyBackstopsForAgentExclusivityAndIdempotency()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(TeamMembership));
        Assert.NotNull(entity);

        var uniqueIndexes = entity.GetIndexes()
            .Where(x => x.IsUnique)
            .Select(x => x.Properties.Select(property => property.Name).ToArray())
            .ToList();

        Assert.Contains(uniqueIndexes, properties =>
            properties.SequenceEqual([nameof(TeamMembership.ExclusiveAgentEmployeeId)]));
        Assert.Contains(uniqueIndexes, properties =>
            properties.SequenceEqual(
                [nameof(TeamMembership.TeamId), nameof(TeamMembership.OrganizationUserId)]));
    }

    private static TeamService Service(CSweetDbContext db) =>
        new(db, new TestAuditEventWriter(), TimeProvider.System);

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Setup Seed(CSweetDbContext db)
    {
        var organizationId = Guid.NewGuid();
        var managerApplicationUserId = Guid.NewGuid();
        var manager = Employee(organizationId, "Manager", EmployeeType.Human);
        manager.ApplicationUserId = managerApplicationUserId;
        manager.PermissionLevel = OrganizationPermissionLevel.Manager;
        var contributor = Employee(organizationId, "Contributor", EmployeeType.Human);
        contributor.ApplicationUserId = Guid.NewGuid();
        contributor.PermissionLevel = OrganizationPermissionLevel.Contributor;
        var human = Employee(organizationId, "Contractor", EmployeeType.Human);
        var agent = Employee(organizationId, "Developer instance", EmployeeType.Agent);
        agent.AgentInstallationId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Example",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.AddRange(manager, contributor, human, agent);
        return new Setup(organizationId, managerApplicationUserId, manager, contributor, human, agent);
    }

    private static OrganizationUser Employee(Guid organizationId, string name, EmployeeType type) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        DisplayName = name,
        EmployeeType = type,
        PermissionLevel = OrganizationPermissionLevel.Contributor,
        CreatedAt = DateTimeOffset.UtcNow,
        IsActive = true
    };

    private sealed record Setup(
        Guid OrganizationId,
        Guid ManagerApplicationUserId,
        OrganizationUser Manager,
        OrganizationUser Contributor,
        OrganizationUser Human,
        OrganizationUser Agent);
}
