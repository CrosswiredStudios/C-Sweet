using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using DeliveryContracts = CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

/// <summary>
/// A Producer that manages a project but doesn't lead its team creates the project board directly. The board must
/// come with the manager's project delivery access, or every delivery read is denied and staffing never starts
/// (Prism Break, 2026-10-08).
/// </summary>
public sealed class ProjectBoardAccessTests
{
    private sealed record Seeded(CSweetDbContext Db, Guid Org, OrganizationTeam Team, Workstream Project, OrganizationUser Producer,
        OrganizationUser Developer)
    {
        public ProjectWorkPolicy Policy => new(Db, TimeProvider.System);
        public ProjectSetupService Setup => new(Db, TimeProvider.System, Policy);
        public WorkDeliveryService Delivery => new(Db, new ScopedActionAuthorizationService(Db), null!, TimeProvider.System);

        public async Task<WorkBoard> CreateBoardAsync()
        {
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Project.Id, TeamId = Team.Id,
                ManagerOrganizationUserId = Producer.Id, Name = "Game", Key = "VGGAME" };
            Db.WorkBoards.Add(board);
            await Setup.ReconcileProjectBoardAccessAsync(board, default);
            await Db.SaveChangesAsync();
            return board;
        }

        public IEnumerable<string> Actions(OrganizationUser person, GrantScopeKind scope, Guid scopeId) =>
            Db.ScopedActionGrants.Where(x => x.SubjectId == person.AgentInstallationId && x.ScopeKind == scope && x.ScopeId == scopeId &&
                x.RevokedAt == null).Select(x => x.Action).ToList();
    }

    private static async Task<Seeded> SeedAsync()
    {
        var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid();
        OrganizationUser Agent(string name) => new() { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Agent,
            AgentInstallationId = Guid.NewGuid(), IsActive = true, DisplayName = name };
        var producer = Agent("Gabriel");
        var director = Agent("Naomi");
        var developer = Agent("Daniel");
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = org, LeadOrganizationUserId = director.Id, Name = "Video Game Team", TeamKey = "video-game-team" };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, AccountableManagerOrganizationUserId = producer.Id,
            Name = "Prism Break", Outcome = "Ship it", Status = WorkstreamStatus.Approved };
        db.AddRange(producer, director, developer, team, project);
        db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, TeamId = team.Id, StartsAt = DateTimeOffset.UtcNow });
        foreach (var person in new[] { producer, director, developer })
            db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, OrganizationUserId = person.Id, SourceType = "HiringWorkflow" });
        // Hired before the board existed: the participation is recorded, the board grants come later.
        db.ProjectParticipants.Add(new() { OrganizationId = org, WorkstreamId = project.Id, OrganizationUserId = developer.Id,
            AddedByOrganizationUserId = producer.Id, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return new(db, org, team, project, producer, developer);
    }

    [Fact]
    public async Task The_accountable_manager_who_creates_the_board_can_run_delivery_on_it()
    {
        var s = await SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Delivery.ReadAsync(s.Org, s.Producer.AgentInstallationId!.Value, new DeliveryContracts.ReadWorkDeliveryPlansRequest(s.Project.Id)));

        var board = await s.CreateBoardAsync();

        Assert.Contains(s.Db.ProjectParticipants, x => x.OrganizationUserId == s.Producer.Id && x.WorkstreamId == s.Project.Id && x.RemovedAt == null);
        Assert.Equal(DeliveryContracts.WorkDeliveryCapabilities.All.Order(), s.Actions(s.Producer, GrantScopeKind.Workstream, s.Project.Id).Order());
        Assert.Contains(WorkBoardActions.Configure, s.Actions(s.Producer, GrantScopeKind.Board, board.Id));
        Assert.Empty(await s.Delivery.ReadAsync(s.Org, s.Producer.AgentInstallationId!.Value, new DeliveryContracts.ReadWorkDeliveryPlansRequest(s.Project.Id)));
    }

    [Fact]
    public async Task Participants_enrolled_before_the_board_get_board_and_delivery_access_with_it()
    {
        var s = await SeedAsync();
        var board = await s.CreateBoardAsync();

        await s.Policy.RequireAsync(s.Org, s.Developer.Id, board.Id, default);
        Assert.Equal(new[] { DeliveryContracts.WorkDeliveryCapabilities.Evidence, DeliveryContracts.WorkDeliveryCapabilities.Read, DeliveryContracts.WorkDeliveryCapabilities.Review }.Order(),
            s.Actions(s.Developer, GrantScopeKind.Workstream, s.Project.Id).Order());
        Assert.DoesNotContain(WorkBoardActions.Configure, s.Actions(s.Developer, GrantScopeKind.Board, board.Id));
        Assert.Empty(await s.Delivery.ReadAsync(s.Org, s.Developer.AgentInstallationId!.Value, new DeliveryContracts.ReadWorkDeliveryPlansRequest(s.Project.Id)));
    }

    [Fact]
    public async Task Reconciling_again_adds_nothing_and_never_restores_a_revoked_grant()
    {
        var s = await SeedAsync();
        var board = await s.CreateBoardAsync();
        var grant = s.Db.ScopedActionGrants.Single(x => x.SubjectId == s.Developer.AgentInstallationId &&
            x.ScopeKind == GrantScopeKind.Workstream && x.Action == DeliveryContracts.WorkDeliveryCapabilities.Review);
        grant.RevokedAt = DateTimeOffset.UtcNow;
        await s.Db.SaveChangesAsync();
        var count = s.Db.ScopedActionGrants.Count();

        Assert.Empty(await s.Setup.ReconcileProjectBoardAccessAsync(board, default));
        await s.Db.SaveChangesAsync();

        Assert.Equal(count, s.Db.ScopedActionGrants.Count());
        Assert.Single(s.Db.ProjectParticipants, x => x.OrganizationUserId == s.Producer.Id);
        Assert.NotNull(s.Db.ScopedActionGrants.Single(x => x.Id == grant.Id).RevokedAt);
    }

    [Fact]
    public async Task A_manager_a_human_removed_from_the_project_stays_removed()
    {
        var s = await SeedAsync();
        s.Db.ProjectParticipants.Add(new() { OrganizationId = s.Org, WorkstreamId = s.Project.Id, OrganizationUserId = s.Producer.Id,
            AddedByOrganizationUserId = s.Producer.Id, JoinedAt = DateTimeOffset.UtcNow, RemovedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        await s.CreateBoardAsync();

        Assert.NotNull(s.Db.ProjectParticipants.Single(x => x.OrganizationUserId == s.Producer.Id).RemovedAt);
        Assert.Empty(s.Actions(s.Producer, GrantScopeKind.Workstream, s.Project.Id));
    }

    [Fact]
    public async Task A_manager_working_on_another_active_project_is_left_for_a_decision()
    {
        var s = await SeedAsync();
        var elsewhere = new Workstream { Id = Guid.NewGuid(), OrganizationId = s.Org, Name = "Other", Outcome = "Other", Status = WorkstreamStatus.Active };
        s.Db.Workstreams.Add(elsewhere);
        s.Db.ProjectParticipants.Add(new() { OrganizationId = s.Org, WorkstreamId = elsewhere.Id, OrganizationUserId = s.Producer.Id,
            AddedByOrganizationUserId = s.Producer.Id, JoinedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        await s.CreateBoardAsync();

        Assert.DoesNotContain(s.Db.ProjectParticipants, x => x.OrganizationUserId == s.Producer.Id && x.WorkstreamId == s.Project.Id);
        Assert.Empty(s.Actions(s.Producer, GrantScopeKind.Workstream, s.Project.Id));
    }

    [Fact]
    public async Task A_board_for_an_inactive_project_or_no_project_changes_nothing()
    {
        var s = await SeedAsync();
        s.Project.Status = WorkstreamStatus.Completed;
        await s.Db.SaveChangesAsync();
        await s.CreateBoardAsync();
        var team = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = s.Org, TeamId = s.Team.Id, ManagerOrganizationUserId = s.Producer.Id, Name = "Team", Key = "TEAM" };
        Assert.Empty(await s.Setup.ReconcileProjectBoardAccessAsync(team, default));

        Assert.Single(s.Db.ProjectParticipants);
        Assert.Empty(s.Db.ScopedActionGrants);
    }
}
