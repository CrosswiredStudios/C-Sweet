using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

/// <summary>A staffing hire joins the team to deliver its project, so it becomes a project participant.</summary>
public sealed class ProjectHireEnrollmentTests
{
    private sealed record Seeded(CSweetDbContext Db, Guid Org, OrganizationTeam Team, Workstream Project, OrganizationUser Developer, WorkBoard? Board)
    {
        public ProjectWorkPolicy Policy => new(Db, TimeProvider.System);
        public ProjectSetupService Setup => new(Db, TimeProvider.System, Policy);
        public Task<bool> EnrollAsync(Guid? projectId = null) =>
            Setup.EnrollHiredTeamMemberAsync(Org, Team.Id, Developer.Id, projectId, default);
    }

    private static async Task<Seeded> SeedAsync(string source = "HiringWorkflow", bool withBoard = true)
    {
        var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid();
        var producer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid(), IsActive = true, DisplayName = "Producer" };
        var lead = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid(), IsActive = true, DisplayName = "Director" };
        var developer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid(), IsActive = true, DisplayName = "Developer" };
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = org, LeadOrganizationUserId = lead.Id, Name = "Video Game Team", TeamKey = "video-game-team" };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, AccountableManagerOrganizationUserId = producer.Id, Name = "Game", Outcome = "Ship it", Status = WorkstreamStatus.Approved };
        db.AddRange(producer, lead, developer, team, project);
        db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, TeamId = team.Id, StartsAt = DateTimeOffset.UtcNow });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, OrganizationUserId = lead.Id, SourceType = "ApprovedResourceChange" });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, OrganizationUserId = developer.Id, SourceType = source });
        WorkBoard? board = null;
        if (withBoard)
        {
            board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, TeamId = team.Id, ManagerOrganizationUserId = producer.Id, Name = "Game", Key = "VGGAME" };
            db.WorkBoards.Add(board);
        }
        await db.SaveChangesAsync();
        return new(db, org, team, project, developer, board);
    }

    private static WorkBoard AddBoard(Seeded s)
    {
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = s.Org, WorkstreamId = s.Project.Id, TeamId = s.Team.Id,
            ManagerOrganizationUserId = s.Project.AccountableManagerOrganizationUserId!.Value, Name = "Game", Key = "VGGAME" };
        s.Db.WorkBoards.Add(board);
        return board;
    }

    [Fact]
    public async Task A_hire_for_the_team_becomes_a_participant_with_board_access()
    {
        var s = await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Policy.RequireAsync(s.Org, s.Developer.Id, s.Board!.Id, default));

        Assert.True(await s.EnrollAsync());
        await s.Db.SaveChangesAsync();

        var participant = Assert.Single(s.Db.ProjectParticipants);
        Assert.Equal(s.Developer.Id, participant.OrganizationUserId);
        Assert.Equal(s.Project.AccountableManagerOrganizationUserId, participant.AddedByOrganizationUserId);
        Assert.Contains(s.Db.ScopedActionGrants, x => x.SubjectId == s.Developer.AgentInstallationId && x.ScopeKind == GrantScopeKind.Board &&
            x.ScopeId == s.Board!.Id && x.Action == WorkItemActions.Read && x.RevokedAt == null);
        Assert.DoesNotContain(s.Db.ScopedActionGrants, x => x.SubjectId == s.Developer.AgentInstallationId && x.Action == "work.item.move");
        Assert.Single(s.Db.AgentPlatformEventOutbox, x => x.TargetInstallationId == s.Developer.AgentInstallationId);
        await s.Policy.RequireAsync(s.Org, s.Developer.Id, s.Board!.Id, default);

        Assert.True(await s.EnrollAsync());
        await s.Db.SaveChangesAsync();
        Assert.Single(s.Db.ProjectParticipants);
        Assert.Single(s.Db.AgentPlatformEventOutbox, x => x.TargetInstallationId == s.Developer.AgentInstallationId);
    }

    [Fact]
    public async Task A_hire_made_before_the_board_exists_gets_board_access_once_it_does()
    {
        var s = await SeedAsync(withBoard: false);
        Assert.True(await s.EnrollAsync());
        await s.Db.SaveChangesAsync();
        Assert.Single(s.Db.ProjectParticipants);
        Assert.Empty(s.Db.ScopedActionGrants);

        var board = AddBoard(s);
        await s.Db.SaveChangesAsync();
        Assert.True(await s.EnrollAsync(s.Project.Id));
        await s.Db.SaveChangesAsync();
        await s.Policy.RequireAsync(s.Org, s.Developer.Id, board.Id, default);
    }

    [Fact]
    public async Task A_revoked_board_grant_is_not_restored()
    {
        var s = await SeedAsync(withBoard: false);
        Assert.True(await s.EnrollAsync());
        var board = AddBoard(s);
        s.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = s.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = s.Developer.AgentInstallationId!.Value, Action = WorkItemActions.Read, ScopeKind = GrantScopeKind.Board, ScopeId = board.Id,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser, GrantedBySubjectId = s.Project.AccountableManagerOrganizationUserId!.Value,
            GrantedAt = DateTimeOffset.UtcNow, RevokedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        Assert.True(await s.EnrollAsync(s.Project.Id));
        await s.Db.SaveChangesAsync();
        Assert.NotNull(s.Db.ScopedActionGrants.Single(x => x.Action == WorkItemActions.Read).RevokedAt);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Policy.RequireAsync(s.Org, s.Developer.Id, board.Id, default));
    }

    [Fact]
    public async Task A_participant_a_human_removed_stays_removed()
    {
        var s = await SeedAsync();
        s.Db.ProjectParticipants.Add(new() { OrganizationId = s.Org, WorkstreamId = s.Project.Id, OrganizationUserId = s.Developer.Id,
            AddedByOrganizationUserId = s.Project.AccountableManagerOrganizationUserId!.Value, JoinedAt = DateTimeOffset.UtcNow, RemovedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        Assert.False(await s.EnrollAsync());
        await s.Db.SaveChangesAsync();
        Assert.NotNull(s.Db.ProjectParticipants.Single().RemovedAt);
        Assert.Empty(s.Db.ScopedActionGrants);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("ApprovedResourceChange")]
    public async Task Members_who_were_not_hired_for_the_team_are_not_enrolled(string source)
    {
        var s = await SeedAsync(source);
        Assert.False(await s.EnrollAsync());
        await s.Db.SaveChangesAsync();
        Assert.Empty(s.Db.ProjectParticipants);
    }

    [Fact]
    public async Task A_team_with_several_active_projects_needs_the_exact_project()
    {
        var s = await SeedAsync();
        var other = new Workstream { Id = Guid.NewGuid(), OrganizationId = s.Org, AccountableManagerOrganizationUserId = s.Project.AccountableManagerOrganizationUserId,
            Name = "Sequel", Outcome = "Ship it again", Status = WorkstreamStatus.Active };
        s.Db.Workstreams.Add(other);
        s.Db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = s.Org, WorkstreamId = other.Id, TeamId = s.Team.Id, StartsAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        Assert.False(await s.EnrollAsync());
        Assert.True(await s.EnrollAsync(s.Project.Id));
        await s.Db.SaveChangesAsync();
        Assert.Equal(s.Project.Id, s.Db.ProjectParticipants.Single().WorkstreamId);
    }

    [Fact]
    public async Task A_hire_already_working_on_another_active_project_is_left_for_a_decision()
    {
        var s = await SeedAsync();
        var elsewhere = new Workstream { Id = Guid.NewGuid(), OrganizationId = s.Org, Name = "Other", Outcome = "Other", Status = WorkstreamStatus.Active };
        s.Db.Workstreams.Add(elsewhere);
        s.Db.ProjectParticipants.Add(new() { OrganizationId = s.Org, WorkstreamId = elsewhere.Id, OrganizationUserId = s.Developer.Id,
            AddedByOrganizationUserId = s.Developer.Id, JoinedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        Assert.False(await s.EnrollAsync());
        await s.Db.SaveChangesAsync();
        Assert.DoesNotContain(s.Db.ProjectParticipants, x => x.WorkstreamId == s.Project.Id);
    }
}
