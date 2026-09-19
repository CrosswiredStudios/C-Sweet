using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ProjectSetupTests
{
    private static CSweetDbContext Db() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static (OrganizationUser Human, OrganizationUser Developer) Seed(CSweetDbContext db)
    {
        var org = Guid.NewGuid();
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, ApplicationUserId = Guid.NewGuid(), DisplayName = "Human", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        var developer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = Guid.NewGuid(), DisplayName = "Developer", EmployeeType = EmployeeType.Agent };
        db.CoreOrganizationUsers.AddRange(human, developer); return (human, developer);
    }
    private static ProjectSetupService Service(CSweetDbContext db) => new(db, TimeProvider.System, new(db, TimeProvider.System));
    private static CreateProjectRequest Request(OrganizationUser human, OrganizationUser developer, string key = "request") =>
        new("Prototype", "Build a working prototype", human.Id, null, [developer.Id], null, null, null, key);

    [Fact]
    public async Task Creation_is_idempotent_and_membership_does_not_change_reporting_lines()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var service = Service(db); var request = Request(human, developer);
        var created = await service.CreateAsync(human, request, default);
        Assert.Equal(created, await service.CreateAsync(human, request, default));
        Assert.Single(db.Workstreams); Assert.Single(db.WorkBoards); Assert.Equal(2, db.ProjectParticipants.Count());
        Assert.Null(developer.ReportsToOrganizationUserId);
        await new ProjectWorkPolicy(db, TimeProvider.System).RequireAsync(human.OrganizationId, developer.Id, created.BoardId, default);
    }
    [Fact]
    public async Task Existing_team_is_reused_without_assigning_other_members_or_changing_its_lead()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        var other = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, EmployeeType = EmployeeType.Human };
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, Name = "Existing", LeadOrganizationUserId = other.Id };
        db.CoreOrganizationUsers.Add(other); db.OrganizationTeams.Add(team);
        db.TeamMemberships.AddRange(new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, TeamId = team.Id, OrganizationUserId = developer.Id, ExclusiveAgentEmployeeId = developer.Id },
            new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, TeamId = team.Id, OrganizationUserId = other.Id });
        await db.SaveChangesAsync(); var result = await Service(db).CreateAsync(human, Request(human, developer), default);
        Assert.Single(db.OrganizationTeams); Assert.Equal(team.Id, (await db.WorkBoards.SingleAsync()).TeamId); Assert.Equal(other.Id, team.LeadOrganizationUserId);
        Assert.False(await db.ProjectParticipants.AnyAsync(x => x.OrganizationUserId == other.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectWorkPolicy(db, TimeProvider.System).RequireAsync(human.OrganizationId, other.Id, result.BoardId, default));
    }
    [Fact]
    public async Task Removing_assignment_revokes_access_and_blocks_execution()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync(); var service = Service(db);
        var result = await service.CreateAsync(human, Request(human, developer), default);
        await service.UpdateMembersAsync(human, result.ProjectId, new(human.Id, [human.Id], result.Revision), default);
        Assert.NotNull((await db.ProjectParticipants.SingleAsync(x => x.OrganizationUserId == developer.Id)).RemovedAt);
        Assert.All(await db.ScopedActionGrants.Where(x => x.SubjectId == developer.AgentInstallationId).ToListAsync(), x => Assert.NotNull(x.RevokedAt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectWorkPolicy(db, TimeProvider.System).RequireAsync(human.OrganizationId, developer.Id, result.BoardId, default));
    }
    [Fact]
    public async Task Agent_cannot_use_human_creation_service()
    {
        await using var db = Db(); var (human, developer) = Seed(db); developer.PermissionLevel = OrganizationPermissionLevel.Owner; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db).CreateAsync(developer, Request(human, developer), default));
        Assert.Empty(db.Workstreams);
    }
    [Fact]
    public async Task Stale_or_cancelled_intake_cannot_create_a_project()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, DeveloperId = developer.Id, RequestingHumanId = human.Id, Status = "Cancelled", Revision = 2 };
        db.ProjectIntakes.Add(intake); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Service(db).CreateAsync(human, Request(human, developer) with { IntakeId = intake.Id, IntakeRevision = 1 }, default));
        Assert.Empty(db.Workstreams);
    }
    [Fact]
    public async Task Incompatible_team_is_rejected_without_moving_agent()
    {
        await using var db = Db(); var (human, developer) = Seed(db); var oldTeam = Guid.NewGuid();
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, TeamId = oldTeam, OrganizationUserId = developer.Id, ExclusiveAgentEmployeeId = developer.Id, EndedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateAsync(human, Request(human, developer) with { TeamId = Guid.NewGuid() }, default));
        Assert.Contains("one team", error.Message); Assert.Equal(oldTeam, (await db.TeamMemberships.SingleAsync()).TeamId);
    }

    [Theory]
    [InlineData("{\"rolePolicy\":{\"requiresProject\":true,\"declaredRoleKeys\":[\"audio-designer\"]}}", true)]
    [InlineData("{\"rolePolicy\":{\"requiresProject\":false}}", false)]
    [InlineData("{}", false)]
    [InlineData("", false)]
    public async Task Requirement_comes_from_manifest_not_agent_name_or_role(string manifest, bool expected)
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        db.AgentInstallations.Add(new() { Id = developer.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"), IsEnabled = true,
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = manifest } });
        await db.SaveChangesAsync();
        Assert.Equal(expected, await new ProjectWorkPolicy(db, TimeProvider.System).RequiresProjectAsync(human.OrganizationId, developer.AgentInstallationId.Value, default));
    }
    [Fact]
    public async Task Requiring_agent_cannot_start_projectless_delivery_but_can_keep_personal_reminders()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        db.AgentInstallations.Add(new() { Id = developer.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"), IsEnabled = true,
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}" } });
        await db.SaveChangesAsync(); var policy = new ProjectWorkPolicy(db, TimeProvider.System);
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, AssignedEmployeeId = developer.Id, AssignedAgentInstallationId = developer.AgentInstallationId, Description = "Remember tomorrow's meeting" };
        await policy.RequireIfConfiguredAsync(item, default);
        item.Description = "csweet-direct-development-v1";
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.RequireIfConfiguredAsync(item, default));
        db.LegacyDevelopmentAuthorizations.Add(new() { WorkItemId = item.Id, OrganizationId = human.OrganizationId }); await db.SaveChangesAsync();
        await policy.RequireIfConfiguredAsync(item, default);
        item.Id = Guid.NewGuid(); // New work cannot inherit an old execution exception.
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.RequireIfConfiguredAsync(item, default));
    }
    [Fact]
    public async Task Revoked_grant_and_completed_project_prevent_development()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var service = Service(db); var result = await service.CreateAsync(human, Request(human, developer), default);
        var grant = await db.ScopedActionGrants.SingleAsync(x => x.SubjectId == developer.AgentInstallationId && x.Action == CSweet.Contracts.WorkManagement.WorkItemActions.Read);
        grant.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProjectWorkPolicy(db, TimeProvider.System).RequireAsync(human.OrganizationId, developer.Id, result.BoardId, default));
        grant.RevokedAt = null; await db.SaveChangesAsync();
        await service.ChangeStatusAsync(human, result.ProjectId, new("Completed", result.Revision), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectWorkPolicy(db, TimeProvider.System).RequireAsync(human.OrganizationId, developer.Id, result.BoardId, default));
    }
    [Fact]
    public async Task Edited_intake_members_keep_request_waiting_and_event_is_atomic_with_state()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, DeveloperId = developer.Id, DeveloperInstallationId = developer.AgentInstallationId!.Value, RequestingHumanId = human.Id, Status = "AwaitingProjectCreation", TicketOwner = "ask" };
        db.ProjectIntakes.Add(intake); await db.SaveChangesAsync();
        await Service(db).CreateAsync(human, Request(human, developer) with { MemberIds = [human.Id], IntakeId = intake.Id, IntakeRevision = 1 }, default);
        Assert.Equal("AwaitingAssignment", intake.Status); Assert.Equal("ask", intake.TicketOwner);
        Assert.False(await db.ProjectParticipants.AnyAsync(x => x.OrganizationUserId == developer.Id));
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.TargetInstallationId == developer.AgentInstallationId).ToListAsync());
    }
    [Fact]
    public async Task Agent_manager_capacity_is_released_on_completion_and_rechecked_on_reopening()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        db.AgentInstallations.Add(new() { Id = developer.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"), IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active,
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-product-manager\"]}}" } });
        await db.SaveChangesAsync(); var service = Service(db);
        var first = await service.CreateAsync(human, Request(human, developer) with { ManagerId = developer.Id }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReserveManagerAsync(human.OrganizationId, developer.Id, Guid.NewGuid(), null, default));
        await service.ChangeStatusAsync(human, first.ProjectId, new("Completed", first.Revision), default);
        Assert.Empty(db.ProjectManagerReservations);
        var second = await service.CreateAsync(human, Request(human, developer, "second") with { ManagerId = developer.Id }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeStatusAsync(human, first.ProjectId, new("Active", 2), default));
        Assert.Equal(second.ProjectId, (await db.ProjectManagerReservations.SingleAsync()).WorkstreamId);
    }
    [Fact]
    public async Task Ready_intake_creates_one_project_hierarchy_and_revoked_assignment_stops_planning()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        db.AgentInstallations.Add(new() { Id = developer.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"), IsEnabled = true,
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}" } });
        var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, DeveloperId = developer.Id,
            DeveloperInstallationId = developer.AgentInstallationId.Value, RequestingHumanId = human.Id, Name = "Prototype", Goal = "Build a playable game",
            Status = "AwaitingProjectCreation", TicketOwner = "self" };
        db.ProjectIntakes.Add(intake); await db.SaveChangesAsync();
        var project = await Service(db).CreateAsync(human, Request(human, developer) with { IntakeId = intake.Id, IntakeRevision = intake.Revision }, default);
        Assert.Equal("Ready", intake.Status);
        var engine = new CSweet.Infrastructure.WorkManagement.WorkItemMutationEngine(db, TimeProvider.System);
        var start = new CSweet.Agent.SDK.StartProjectIntakeRequest(intake.Id, intake.Revision, "start");
        var root = await engine.StartProjectIntakeAsync(human.OrganizationId, developer.AgentInstallationId.Value, start, default);
        Assert.Equal(root.Id, (await engine.StartProjectIntakeAsync(human.OrganizationId, developer.AgentInstallationId.Value, start, default)).Id);
        Assert.Equal(project.BoardId, (await db.CoreWorkTasks.SingleAsync()).BoardId);
        var stored = await db.CoreWorkTasks.SingleAsync();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = Guid.NewGuid(); stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        stored.BoardColumnId = await db.WorkBoardColumns.Where(x => x.BoardId == project.BoardId && x.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.InProgress).Select(x => x.Id).SingleAsync();
        await db.SaveChangesAsync();
        var actor = new CSweet.Application.WorkManagement.PersonalTodoActor(developer.Id, developer.AgentInstallationId);
        var planRequest = new CSweet.WorkManagement.Contracts.CreatePersonalWorkPlanRequest(root.Id, "Playable prototype",
            [new("game", "Game", "Working game", ["Playable"], [new("code", "Code", "Implement game", ["Tests pass"]), new("tests", "Tests", "Test game", ["Tests pass"])]), new("delivery", "Delivery", "Working URL", ["Available"], [new("validate", "Validate", "Validate game", ["Checks pass"], "Validation"), new("deploy", "Deploy", "Publish game", ["Review URL works"], "Deployment")])], "plan");
        var plan = await engine.CreatePlanAsync(human.OrganizationId, actor, planRequest);
        Assert.Equal(plan.Items.Select(x => x.Id), (await engine.CreatePlanAsync(human.OrganizationId, actor, planRequest)).Items.Select(x => x.Id));
        Assert.All(await db.CoreWorkTasks.ToListAsync(), x => Assert.Equal(project.BoardId, x.BoardId));
        Assert.Equal("Started", intake.Status);
        var directory = await engine.ListAsync(human.OrganizationId, actor);
        var view = Assert.Single(directory.Boards, x => x.OwnerOrganizationUserId == developer.Id);
        Assert.Equal(7, view.Items.Count);
        Assert.All(view.Items, x => Assert.Equal(project.BoardId, x.BoardId));
        Assert.Equal(7, await db.CoreWorkTasks.CountAsync());
        await engine.BlockAsync(human.OrganizationId, actor, new(root.Id, stored.ClaimEventId!.Value, stored.Revision, "Compiler failed: missing generated source", "block-project"));
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal($"/organizations/{human.OrganizationId:D}/projects/{project.ProjectId:D}", notification.ActionUri);
        Assert.Contains("missing generated source", notification.Body);
        await Service(db).UpdateMembersAsync(human, project.ProjectId, new(human.Id, [human.Id], project.Revision), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.CreatePlanAsync(human.OrganizationId, actor, planRequest));
        Assert.Equal(7, await db.CoreWorkTasks.CountAsync());
    }

    [Fact]
    public async Task Old_manager_proposal_cannot_authorize_a_new_setup_choice()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, ManagerId = developer.Id,
            Status = "AwaitingManagerAssistance", LastChoiceMessageId = Guid.NewGuid() };
        db.ProjectIntakes.Add(intake); await db.SaveChangesAsync();
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, AccountableManagerOrganizationUserId = developer.Id };
        var stale = System.Text.Json.JsonSerializer.SerializeToElement(new { intakeId = intake.Id, setupChoiceMessageId = Guid.NewGuid(), participantIds = new[] { developer.Id, human.Id } });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).ProvisionApprovedAsync(project, human, null, stale, default));
        Assert.Contains("obsolete", error.Message); Assert.Empty(db.ProjectManagerReservations); Assert.Empty(db.WorkBoards);
    }

    [Fact]
    public async Task Intake_requires_an_addressed_human_message_and_keeps_original_request_without_creating_work()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), SenderOrganizationUserId = human.Id, Content = "Please build my game, including keyboard controls." };
        db.CoreConversationMessages.Add(message);
        db.ChatTurns.Add(new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, ConversationId = message.ConversationId, UserMessageId = message.Id, TargetAgentOrganizationUserId = developer.Id });
        await db.SaveChangesAsync();
        var service = new ProjectIntakeService(db, TimeProvider.System, Service(db), new(db, TimeProvider.System), null!, null!);
        var request = new CSweet.Agent.SDK.RetainProjectIntakeRequest(message.ConversationId, message.Id, "Game", "Playable game", "ask", null, "retain");
        var intake = await service.RetainAsync(human.OrganizationId, developer.AgentInstallationId!.Value, request, default);
        Assert.Equal(message.Content, intake.OriginalRequest); Assert.Equal("AwaitingProjectChoice", intake.Status);
        Assert.Equal(intake.Id, (await service.RetainAsync(human.OrganizationId, developer.AgentInstallationId.Value, request with { IdempotencyKey = "retry-new-key" }, default)).Id);
        Assert.Empty(db.CoreWorkTasks); Assert.Empty(db.Workstreams); Assert.Single(db.ProjectIntakes);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadAsync(human.OrganizationId, Guid.NewGuid(), intake.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetainAsync(human.OrganizationId, developer.AgentInstallationId.Value, request with { SourceMessageId = Guid.NewGuid() }, default));
        var choice = new CSweet.Agent.SDK.ChooseProjectIntakeRequest(intake.Id, intake.Revision, "manager", null, message.Id, "manager");
        var waiting = await service.RequestManagerAsync(human.OrganizationId, developer.AgentInstallationId.Value, choice, default);
        Assert.Equal("AwaitingManagerAssistance", waiting.Status); Assert.Contains("Chief of Staff", waiting.Issue);
        Assert.Null((await db.ProjectIntakes.SingleAsync()).HiringRecommendationId);
        var cancelled = await service.ChooseAsync(human.OrganizationId, developer.AgentInstallationId.Value, choice with { ExpectedRevision = waiting.Revision, Choice = "cancel", IdempotencyKey = "cancel" }, default);
        Assert.Equal("Cancelled", cancelled.Status);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ChooseAsync(human.OrganizationId, developer.AgentInstallationId.Value, choice with { Choice = "create", IdempotencyKey = "late" }, default));
        Assert.Empty(await service.ListAsync(human.OrganizationId, developer.AgentInstallationId.Value, default));
    }

    [Fact]
    public async Task Discovery_keeps_duplicate_project_names_distinct_and_naming_does_not_assign_developer()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var first = await Service(db).CreateAsync(human, Request(human, developer, "first") with { MemberIds = [human.Id] }, default);
        var second = await Service(db).CreateAsync(human, Request(human, developer, "second") with { MemberIds = [human.Id] }, default);
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), SenderOrganizationUserId = human.Id, Content = "Use Prototype" };
        db.CoreConversationMessages.Add(message);
        db.ChatTurns.Add(new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, ConversationId = message.ConversationId, UserMessageId = message.Id, TargetAgentOrganizationUserId = developer.Id });
        var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, DeveloperId = developer.Id, DeveloperInstallationId = developer.AgentInstallationId!.Value,
            RequestingHumanId = human.Id, ConversationId = message.ConversationId, SourceMessageId = message.Id };
        db.ProjectIntakes.Add(intake); await db.SaveChangesAsync();
        var service = new ProjectIntakeService(db, TimeProvider.System, Service(db), new(db, TimeProvider.System), null!, null!);
        var found = await service.DiscoverAsync(human.OrganizationId, developer.AgentInstallationId.Value, intake.Id, default);
        Assert.Equal(2, found.Count); Assert.All(found, x => { Assert.Equal("Prototype", x.Name); Assert.False(x.DeveloperAssigned); });
        var selected = await service.ChooseAsync(human.OrganizationId, developer.AgentInstallationId.Value, new(intake.Id, intake.Revision, "existing", second.ProjectId, message.Id, "select"), default);
        Assert.Equal("AwaitingAssignment", selected.Status); Assert.Contains("/members?intake=", selected.SetupUrl);
        Assert.False(await db.ProjectParticipants.AnyAsync(x => x.OrganizationUserId == developer.Id)); Assert.Empty(db.CoreWorkTasks);
    }

    [Fact]
    public async Task Queued_request_moves_through_setup_and_reuses_its_ticket()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        db.AgentInstallations.Add(new() { Id = developer.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"), IsEnabled = true,
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}" } });
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), SenderOrganizationUserId = human.Id, Content = "Build a game" };
        db.CoreConversationMessages.Add(message);
        db.ChatTurns.Add(new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, ConversationId = message.ConversationId, UserMessageId = message.Id, TargetAgentOrganizationUserId = developer.Id });
        await db.SaveChangesAsync();
        var engine = new CSweet.Infrastructure.WorkManagement.WorkItemMutationEngine(db, TimeProvider.System);
        await engine.EnsureBoardAsync(human.OrganizationId, developer.Id);
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync();
        var queued = new WorkTask { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, BoardId = board.Id,
            BoardColumnId = board.Columns.First().Id, AssignedEmployeeId = developer.Id, AssignedAgentInstallationId = developer.AgentInstallationId,
            SourceConversationId = message.ConversationId, SourceMessageId = message.Id, Title = "Game", Status = WorkTaskStatus.Ready,
            Description = "{\"kind\":\"csweet-direct-development-v1\",\"request\":\"Build a game\"}" };
        db.CoreWorkTasks.Add(queued); await db.SaveChangesAsync();
        await engine.RetainUnstartedProjectRequestsAsync(default); await engine.RetainUnstartedProjectRequestsAsync(default);
        var intake = await db.ProjectIntakes.SingleAsync(); Assert.Equal(queued.Id, intake.PendingWorkItemId);
        Assert.Equal(WorkTaskStatus.Blocked, queued.Status); Assert.Empty(db.Workstreams);
        var project = await Service(db).CreateAsync(human, Request(human, developer) with { IntakeId = intake.Id, IntakeRevision = intake.Revision }, default);
        var started = await engine.StartProjectIntakeAsync(human.OrganizationId, developer.AgentInstallationId.Value, new(intake.Id, intake.Revision, "resume"), default);
        Assert.Equal(queued.Id, started.Id); Assert.Equal(project.BoardId, started.BoardId); Assert.Equal("Ready", started.Status);
        Assert.Single(db.CoreWorkTasks); Assert.Single(db.ProjectIntakes);
    }

    [Fact]
    public async Task Follow_up_epic_reserves_the_same_project_repository_even_when_its_title_changes()
    {
        await using var db = Db(); var (human, developer) = Seed(db);
        const string reserve = "source-control.personal-work.reserve.v1";
        db.AgentInstallations.Add(new() { Id = developer.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"),
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}" },
            Grant = new() { Id = Guid.NewGuid(), RequiredCapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(new[] { reserve }) } });
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, Provider = SourceControlProvider.InternalGit, Status = SourceControlConnectionStatus.Connected };
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, ConnectionId = connection.Id, Connection = connection, Status = SourceControlRepositoryStatus.Ready, Name = "original-game" };
        db.SourceControlConnections.Add(connection); db.SourceControlRepositories.Add(repository); await db.SaveChangesAsync();
        var project = await Service(db).CreateAsync(human, Request(human, developer) with { RepositoryId = repository.Id }, default);
        var engine = new CSweet.Infrastructure.WorkManagement.WorkItemMutationEngine(db, TimeProvider.System);
        var handler = new CSweet.AgentHost.Broker.GitWorkspaceCapabilityHandler(db, new CSweet.AgentHost.Broker.UnavailableTrustedGitHostClient(), null!, null!, WorkspaceSyncTestOptions.Value);
        var session = new CSweet.AgentHost.Broker.AgentSession("session", "developer", developer.AgentInstallationId.Value.ToString(), human.OrganizationId.ToString(), "runtime", "tick",
            new CSweet.AgentHost.Broker.AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { reserve }, 1));
        foreach (var title in new[] { "Breakout game", "Repair level one bricks" })
        {
            var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, DeveloperId = developer.Id, DeveloperInstallationId = developer.AgentInstallationId.Value,
                RequestingHumanId = human.Id, ProjectId = project.ProjectId, BoardId = project.BoardId, Status = "Ready", TicketOwner = "self", Name = title, Goal = title,
                SourceMessageId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), IdempotencyKey = title };
            db.ProjectIntakes.Add(intake); await db.SaveChangesAsync();
            var root = await engine.StartProjectIntakeAsync(human.OrganizationId, developer.AgentInstallationId.Value, new(intake.Id, 1, "start"), default);
            var request = new CSweet.AgentHost.Broker.RequestCapability { RequestId = title, Capability = reserve,
                Payload = CSweet.AgentHost.Broker.JsonPayload.From(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { itemId = root.Id, expectedRevision = root.Revision, suggestedName = title, idempotencyKey = title })) };
            await foreach (var result in handler.HandleAsync(session, request, default))
            {
                Assert.True(result.Succeeded, result.Error);
                using var json = System.Text.Json.JsonDocument.Parse(result.Payload.Span.ToArray());
                Assert.Equal(repository.Id, json.RootElement.GetProperty("repositoryId").GetGuid());
            }
        }
        Assert.Single(db.SourceControlRepositories); Assert.Empty(db.RepositoryProvisioningRequests);
    }

    [Theory]
    [InlineData("work.item.create")]
    [InlineData("source-control.repository.provision.v2")]
    [InlineData("source-control.personal-work.reserve.v1")]
    [InlineData("git.workspace.prepare.v2")]
    public async Task Generic_manifest_policy_rejects_direct_delivery_without_a_project(string capability)
    {
        await using var db = Db(); var (human, agent) = Seed(db);
        db.AgentInstallations.Add(new() { Id = agent.AgentInstallationId!.Value, BusinessId = human.OrganizationId.ToString("D"),
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true,\"declaredRoleKeys\":[\"audio-designer\"]}}" } });
        await db.SaveChangesAsync();
        var policy = new CSweet.AgentHost.Broker.ProjectCapabilityPolicy(db, new(db, TimeProvider.System));
        var input = System.Text.Json.JsonSerializer.SerializeToElement(new { instruction = "skip project setup" });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ValidateAsync(human.OrganizationId, agent.AgentInstallationId.Value, capability, input, default));
        Assert.Contains("project.required", failure.Message);
        await policy.ValidateAsync(human.OrganizationId, agent.AgentInstallationId.Value, "work.personal-todo.add.v1", input, default);
        Assert.Empty(db.CoreWorkTasks); Assert.Empty(db.Workstreams);
    }

}
