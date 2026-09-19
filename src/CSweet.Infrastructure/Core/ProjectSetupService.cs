using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Contracts.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Human-authorized setup. Callers hold the organization transaction lock.</summary>
public sealed partial class ProjectSetupService(CSweetDbContext db, TimeProvider clock, ProjectWorkPolicy policy)
{
    public const string Profile = "software-prototype.v1";
    public async Task<OrganizationUser> HumanAsync(Guid org, Guid applicationUser, CancellationToken ct)
        => await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == org && x.ApplicationUserId == applicationUser &&
            x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, ct)
        ?? throw new UnauthorizedAccessException("An active human organization member is required.");

    public async Task<ProjectSetupOptions> OptionsAsync(OrganizationUser actor, CancellationToken ct)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId && x.IsActive && x.ArchivedAt == null).ToListAsync(ct);
        var members = await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId).ToListAsync(ct);
        var result = new List<ProjectSetupPerson>();
        foreach (var person in people)
            result.Add(new(person.Id, person.DisplayName, person.EmployeeType.ToString(), members.FirstOrDefault(x => x.OrganizationUserId == person.Id)?.TeamId,
                person.EmployeeType == EmployeeType.Human || await HasRoleAsync(person, "software-product-manager", ct)));
        return new(actor.Id, actor.PermissionLevel >= OrganizationPermissionLevel.Manager, result,
            await db.OrganizationTeams.Where(x => x.OrganizationId == actor.OrganizationId && x.ArchivedAt == null).Select(x => new ProjectSetupTeam(x.Id, x.Name)).ToListAsync(ct),
            await db.SourceControlRepositories.Where(x => x.OrganizationId == actor.OrganizationId && x.ArchivedAt == null && x.Status == SourceControlRepositoryStatus.Ready).Select(x => new ProjectSetupRepository(x.Id, x.Name)).ToListAsync(ct));
    }

    public async Task<bool> HasRoleAsync(OrganizationUser person, string role, CancellationToken ct)
    {
        if (!person.AgentInstallationId.HasValue) return false;
        var manifest = await db.AgentInstallations.Where(x => x.Id == person.AgentInstallationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active)
            .Select(x => x.PackageVersion!.ManifestJson).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(manifest)) return false;
        using var json = JsonDocument.Parse(manifest);
        return json.RootElement.TryGetProperty("rolePolicy", out var policyJson) && policyJson.TryGetProperty("declaredRoleKeys", out var roles) && roles.EnumerateArray().Any(x => x.GetString() == role);
    }

    public async Task<ProjectSetupDraft> DraftAsync(OrganizationUser actor, Guid intakeId, CancellationToken ct)
    {
        var intake = await AuthorizedIntakeAsync(actor, intakeId, ct);
        return new(intake.Id, intake.Name, intake.Goal, intake.ManagerId ?? intake.RequestingHumanId, intake.DeveloperId, intake.TeamId, intake.ProjectId, intake.Revision, intake.Status);
    }

    private async Task<ProjectIntake> AuthorizedIntakeAsync(OrganizationUser actor, Guid id, CancellationToken ct)
    {
        var intake = await db.ProjectIntakes.SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("The project request was not found.");
        if (intake.RequestingHumanId != actor.Id && actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("This project request belongs to another person.");
        return intake;
    }

    public async Task<ProjectSetupResult> CreateAsync(OrganizationUser actor, CreateProjectRequest request, CancellationToken ct)
    {
        if (actor.EmployeeType != EmployeeType.Human || actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("Only an authorized human manager can create a project here.");
        Validate(request.Name, request.Goal, request.MemberIds, request.IdempotencyKey);
        var previous = await db.ProjectDeliveryBindings.SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId && x.CreationKey == request.IdempotencyKey, ct);
        if (previous is not null) return new(previous.WorkstreamId, previous.BoardId, previous.Revision);
        ProjectIntake? intake = null;
        if (request.IntakeId is { } id)
        {
            intake = await AuthorizedIntakeAsync(actor, id, ct);
            if (intake.Revision != request.IntakeRevision || intake.Status is "Cancelled" or "Started" || intake.ProjectId.HasValue)
                throw new DbUpdateConcurrencyException("This setup link is no longer current. Reopen the latest request from the developer.");
        }
        var org = actor.OrganizationId;
        var ids = request.MemberIds.Append(request.ManagerId).Distinct().ToArray();
        var people = await ValidatePeopleAsync(org, ids, request.ManagerId, ct);
        var team = await ResolveTeamAsync(org, request.TeamId, people, actor, request.Name, ct);
        if (request.RepositoryId is { } repo && !await db.SourceControlRepositories.AnyAsync(x => x.OrganizationId == org && x.Id == repo && x.ArchivedAt == null && x.Status == SourceControlRepositoryStatus.Ready, ct))
            throw new InvalidOperationException("Select a ready repository in this organization.");
        var now = clock.GetUtcNow();
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Name = request.Name.Trim(), Outcome = request.Goal.Trim(),
            AccountableManagerOrganizationUserId = request.ManagerId, Status = WorkstreamStatus.Active, LifecycleStage = "Development",
            ProfileKey = Profile, ProfileVersion = 1, ProfileDefinitionDigest = SoftwarePrototypeProfile.Digest, ProfileDataJson = "{}", CreatedAt = now, UpdatedAt = now };
        await ReserveManagerAsync(org, request.ManagerId, project.Id, intake?.Id, ct);
        db.Workstreams.Add(project);
        db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, TeamId = team.Id, StartsAt = now });
        var board = CreateBoard(project, team.Id, request.ManagerId);
        db.ProjectDeliveryBindings.Add(new() { WorkstreamId = project.Id, OrganizationId = org, BoardId = board.Id, TeamId = team.Id, RepositoryId = request.RepositoryId, CreationKey = request.IdempotencyKey });
        await ApplyParticipantsAsync(project, board, people, actor, ct);
        if (intake is not null) { intake.ProjectId = project.Id; intake.BoardId = board.Id; intake.TeamId = team.Id; intake.ManagerId = request.ManagerId; }
        await db.SaveChangesAsync(ct);
        await RefreshIntakesAsync(org, project.Id, ct);
        QueueProject(project);
        await db.SaveChangesAsync(ct);
        return new(project.Id, board.Id, 1);
    }

    private WorkBoard CreateBoard(Workstream project, Guid teamId, Guid managerId)
    {
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, WorkstreamId = project.Id, TeamId = teamId,
            ManagerOrganizationUserId = managerId, Name = project.Name, Description = project.Outcome, Key = "P" + project.Id.ToString("N")[..10].ToUpperInvariant(),
            ProfileKey = "general-work.v1", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        var columns = new[] { ("To Do", WorkBoardColumnCategory.ToDo), ("Doing", WorkBoardColumnCategory.InProgress), ("Testing", WorkBoardColumnCategory.Testing), ("Blocked", WorkBoardColumnCategory.Blocked), ("Done", WorkBoardColumnCategory.Done), ("Cancelled", WorkBoardColumnCategory.Cancelled) };
        for (var i = 0; i < columns.Length; i++) board.Columns.Add(new() { Id = Guid.NewGuid(), BoardId = board.Id, Name = columns[i].Item1, Category = columns[i].Item2, Position = i });
        db.WorkBoards.Add(board);
        return board;
    }

    private static void Validate(string name, string goal, IReadOnlyList<Guid> members, string key)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160 || string.IsNullOrWhiteSpace(goal) || goal.Trim().Length > 6000 ||
            members.Count == 0 || members.Count > 100 || string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw new ArgumentException("Provide a name (up to 160 characters), goal (up to 6000), at least one member, and a stable submission key.");
    }

    private async Task<List<OrganizationUser>> ValidatePeopleAsync(Guid org, Guid[] ids, Guid manager, CancellationToken ct)
    {
        var people = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == org && ids.Contains(x.Id) && x.IsActive && x.ArchivedAt == null).ToListAsync(ct);
        if (people.Count != ids.Length) throw new ArgumentException("All selected participants must be active members of this organization.");
        var lead = people.Single(x => x.Id == manager);
        if (lead.EmployeeType != EmployeeType.Human && !await HasRoleAsync(lead, "software-product-manager", ct))
            throw new ArgumentException("Select a human manager or an active software product manager.");
        return people;
    }

    private async Task<OrganizationTeam> ResolveTeamAsync(Guid org, Guid? selected, List<OrganizationUser> people, OrganizationUser actor, string name, CancellationToken ct)
    {
        var agents = people.Where(x => x.EmployeeType == EmployeeType.Agent).Select(x => x.Id).ToArray();
        var existing = await db.TeamMemberships.Where(x => x.OrganizationId == org && agents.Contains(x.OrganizationUserId)).Select(x => x.TeamId).Distinct().ToArrayAsync(ct);
        if (existing.Length > 1 || (selected.HasValue && existing.Any(x => x != selected)))
            throw new InvalidOperationException("An agent can belong to only one team over its lifetime. Select agents from the same team, or hire a new instance; existing agents will not be moved.");
        var teamId = selected ?? existing.Cast<Guid?>().FirstOrDefault();
        OrganizationTeam team;
        if (teamId.HasValue) team = await db.OrganizationTeams.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == teamId && x.ArchivedAt == null, ct)
            ?? throw new InvalidOperationException("The selected team is unavailable.");
        else
        {
            team = new() { Id = Guid.NewGuid(), OrganizationId = org, Name = name.Trim() + " team", LeadOrganizationUserId = actor.Id, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            team.TeamKey = "project-" + team.Id.ToString("N"); team.NormalizedName = team.Name.ToUpperInvariant();
            db.OrganizationTeams.Add(team);
        }
        foreach (var person in people)
        {
            var membership = await db.TeamMemberships.SingleOrDefaultAsync(x => x.TeamId == team.Id && x.OrganizationUserId == person.Id, ct);
            if (membership is null) db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, OrganizationUserId = person.Id,
                ExclusiveAgentEmployeeId = person.EmployeeType == EmployeeType.Agent ? person.Id : null, SourceType = "HumanProjectSetup", JoinedAt = clock.GetUtcNow() });
            else if (membership.EndedAt.HasValue) throw new InvalidOperationException("A selected participant's team membership has ended. Restore membership through team management first.");
        }
        return team;
    }

    public async Task ReserveManagerAsync(Guid org, Guid manager, Guid project, Guid? intake, CancellationToken ct)
    {
        var person = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == org && x.Id == manager, ct);
        if (person.EmployeeType == EmployeeType.Human || !await HasRoleAsync(person, "software-product-manager", ct)) return;
        if (await db.Workstreams.AnyAsync(x => x.OrganizationId == org && x.AccountableManagerOrganizationUserId == manager && x.Id != project &&
            x.Status != WorkstreamStatus.Completed && x.Status != WorkstreamStatus.Cancelled, ct))
            throw new InvalidOperationException("This agent manager already has an active project. Choose an available manager or request another hire.");
        var reservation = await db.ProjectManagerReservations.SingleOrDefaultAsync(x => x.OrganizationUserId == manager, ct);
        if (reservation is not null && reservation.WorkstreamId != project && !(intake.HasValue && reservation.WorkstreamId == null && reservation.IntakeId == intake))
            throw new InvalidOperationException("This agent manager is reserved for another project request.");
        if (reservation is null) db.ProjectManagerReservations.Add(new() { OrganizationUserId = manager, OrganizationId = org, WorkstreamId = project, IntakeId = intake });
        else { reservation.WorkstreamId = project; reservation.IntakeId = intake; reservation.Revision++; }
    }

    private async Task ApplyParticipantsAsync(Workstream project, WorkBoard board, List<OrganizationUser> people, OrganizationUser actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var person in people)
        {
            var participant = await db.ProjectParticipants.SingleOrDefaultAsync(x => x.WorkstreamId == project.Id && x.OrganizationUserId == person.Id, ct);
            if (participant is null) db.ProjectParticipants.Add(new() { OrganizationId = project.OrganizationId, WorkstreamId = project.Id, OrganizationUserId = person.Id, AddedByOrganizationUserId = actor.Id, JoinedAt = now });
            else { participant.RemovedAt = null; participant.Revision++; }
            var subject = person.AgentInstallationId ?? person.Id;
            var kind = person.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
            var manager = person.Id == project.AccountableManagerOrganizationUserId;
            var itemActions = manager || person.EmployeeType == EmployeeType.Human
                ? WorkItemActions.All
                : new[] { WorkItemActions.Read, WorkItemActions.ReadTypes, WorkItemActions.ReadComments, WorkItemActions.Comment,
                    WorkItemActions.Create, WorkItemActions.RevisePlanning, WorkItemActions.Estimate };
            var actions = itemActions.Concat(PersonalTodoActions.All.Where(x => x != PersonalTodoActions.Add)).Append(WorkBoardActions.Read).Distinct().ToArray();
            foreach (var action in actions)
            {
                var grant = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.OrganizationId == project.OrganizationId && x.SubjectId == subject && x.SubjectKind == kind && x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id && x.Action == action, ct);
                if (grant is null) db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, SubjectKind = kind, SubjectId = subject, Action = action,
                    ScopeKind = GrantScopeKind.Board, ScopeId = board.Id, GrantedBySubjectKind = GrantSubjectKind.OrganizationUser, GrantedBySubjectId = actor.Id, GrantedAt = now });
                else { grant.RevokedAt = null; grant.ExpiresAt = null; grant.Revision++; }
            }
        }
    }

    public async Task RefreshIntakesAsync(Guid org, Guid project, CancellationToken ct)
    {
        foreach (var intake in await db.ProjectIntakes.Where(x => x.OrganizationId == org && x.ProjectId == project && x.Status != "Cancelled" && x.Status != "Started").ToListAsync(ct))
        {
            var previous = (intake.BoardId, intake.Status, intake.Issue);
            intake.BoardId = await db.ProjectDeliveryBindings.Where(x => x.WorkstreamId == project && x.OrganizationId == org).Select(x => (Guid?)x.BoardId).SingleOrDefaultAsync(ct);
            try { await policy.RequireAsync(org, intake.DeveloperId, intake.BoardId ?? Guid.Empty, ct); intake.Status = "Ready"; intake.Issue = null; }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException) { intake.Status = "AwaitingAssignment"; intake.Issue = e.Message; }
            if (previous == (intake.BoardId, intake.Status, intake.Issue)) continue;
            intake.Revision++; intake.UpdatedAt = clock.GetUtcNow(); QueueIntake(intake);
        }
    }
    public void QueueIntake(ProjectIntake intake)
    {
        var now = clock.GetUtcNow();
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = intake.OrganizationId, TargetInstallationId = intake.DeveloperInstallationId,
            EventType = ProjectIntakeCapabilities.Changed, DataJson = JsonSerializer.Serialize(new ProjectIntakeChanged(intake.Id, intake.Revision)),
            IdempotencyKey = $"project-intake:{intake.Id:N}:{intake.Revision}", NextAttemptAt = now, OccurredAt = now });
    }
    private void QueueProject(Workstream project)
    {
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId,
            EventType = "com.csweet.workstream.project-setup.v1", Subject = $"organizations/{project.OrganizationId:D}/projects/{project.Id:D}",
            DataJson = JsonSerializer.Serialize(new { project.Id, project.Revision }), NextAttemptAt = clock.GetUtcNow(), OccurredAt = clock.GetUtcNow() });
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, EventType = "com.csweet.workstream.project-setup.v1",
            DataJson = JsonSerializer.Serialize(new { project.Id, project.Revision }), IdempotencyKey = $"project-setup:{project.Id:N}:{project.Revision}", NextAttemptAt = clock.GetUtcNow(), OccurredAt = clock.GetUtcNow() });
    }
}
