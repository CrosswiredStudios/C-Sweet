using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Contracts.Plugins;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ProjectDeliverySetupTests
{
    private static CSweetDbContext Db() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<(OrganizationUser Manager, Workstream Project, PrepareProjectDeliveryRequest Request)> Seed(CSweetDbContext db)
    {
        var org = Guid.NewGuid(); var team = Guid.NewGuid();
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, IsActive = true };
        var developer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, IsActive = true };
        var architect = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, IsActive = true };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, AccountableManagerOrganizationUserId = manager.Id, Name = "Approved demo", Outcome = "Playable demo", Status = WorkstreamStatus.Approved, ProfileKey = "video-game-manager-brief.v1", ProfileVersion = 1 };
        var staffing = new ResourceChangeRequestRecord { Id = Guid.NewGuid(), OrganizationId = org, RequesterInstallationId = manager.AgentInstallationId.Value,
            RequesterOrganizationUserId = manager.Id, Status = ResourceChangeRequestStatus.Approved, TeamId = team };
        db.AddRange(manager, developer, architect, project, staffing, new OrganizationTeam { Id = team, OrganizationId = org, LeadOrganizationUserId = manager.Id, Name = "Approved team", TeamKey = "approved" },
            new WorkstreamAuthorityEnvelopeRecord { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, AgentAuthorizedActionKeysJson = "[\"routine-staffing\"]" });
        foreach (var person in new[] { manager, developer, architect }) db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team, OrganizationUserId = person.Id });
        await db.SaveChangesAsync();
        return (manager, project, new(project.Id, staffing.Id, [developer.Id, architect.Id], project.Revision, "approved-setup"));
    }
    [Fact]
    public async Task Approved_team_attaches_once_with_durable_assignment_events_and_no_new_hires()
    {
        await using var db = Db(); var (manager, project, request) = await Seed(db);
        var service = new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System));
        var result = await service.PrepareDeliveryAsync(manager.OrganizationId, manager.AgentInstallationId!.Value, request, default);
        var grants = db.ScopedActionGrants.Count(); var events = db.AgentPlatformEventOutbox.Count();
        Assert.Equal(result, await service.PrepareDeliveryAsync(manager.OrganizationId, manager.AgentInstallationId.Value, request, default));
        Assert.Single(db.WorkBoards); Assert.Single(db.WorkstreamTeamAssignments); Assert.Single(db.ResourceChangeRequests);
        Assert.Equal(3, db.ProjectParticipants.Count()); Assert.Equal(3, db.TeamMemberships.Count());
        Assert.Equal(grants, db.ScopedActionGrants.Count()); Assert.Equal(events, db.AgentPlatformEventOutbox.Count());
        Assert.Equal(4, events);
        foreach (var action in new[] { "work.orchestration.start", "work.board.columns.configure", "work.flow-metrics.read.v1", "work.item.move" })
            Assert.Contains(db.ScopedActionGrants, x => x.SubjectId == manager.AgentInstallationId && x.Action == action && x.ScopeKind == GrantScopeKind.Board && x.ScopeId == result.BoardId);
        var removed = await db.ProjectParticipants.FirstAsync(x => x.OrganizationUserId != manager.Id); removed.RemovedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareDeliveryAsync(manager.OrganizationId, manager.AgentInstallationId.Value, request, default));
        Assert.NotNull(removed.RemovedAt);
    }
    [Fact]
    public async Task Setup_replay_rejects_a_different_participant_set_with_the_same_size()
    {
        await using var db = Db(); var (manager, project, request) = await Seed(db);
        var service = new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System));
        var receipt = await service.PrepareDeliveryAsync(manager.OrganizationId, manager.AgentInstallationId!.Value, request, default);
        var extra = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = manager.OrganizationId, AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, IsActive = true };
        db.AddRange(extra, new TeamMembership { Id = Guid.NewGuid(), OrganizationId = manager.OrganizationId, TeamId = receipt.TeamId, OrganizationUserId = extra.Id });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareDeliveryAsync(manager.OrganizationId,
            manager.AgentInstallationId.Value, request with { ParticipantIds = [request.ParticipantIds[0], extra.Id] }, default));
        Assert.DoesNotContain(db.ProjectParticipants, x => x.OrganizationUserId == extra.Id);
        Assert.Single(db.WorkBoards);
    }
    [Theory]
    [InlineData("rejected")]
    [InlineData("foreign-manager")]
    [InlineData("foreign-project")]
    [InlineData("ended-member")]
    [InlineData("expired-authority")]
    [InlineData("human-required")]
    [InlineData("stale")]
    public async Task Setup_revalidates_current_authority_before_writing(string scenario)
    {
        await using var db = Db(); var (manager, project, request) = await Seed(db);
        if (scenario == "rejected") db.ResourceChangeRequests.Single().Status = ResourceChangeRequestStatus.Rejected;
        if (scenario == "foreign-manager") project.AccountableManagerOrganizationUserId = Guid.NewGuid();
        if (scenario == "foreign-project") db.ResourceChangeRequests.Single().WorkstreamId = Guid.NewGuid();
        if (scenario == "ended-member") db.TeamMemberships.First().EndedAt = DateTimeOffset.UtcNow;
        if (scenario == "expired-authority") db.WorkstreamAuthorityEnvelopes.Single().ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (scenario == "human-required") db.WorkstreamAuthorityEnvelopes.Single().HumanRequiredActionKeysJson = "[\"routine-staffing\"]";
        if (scenario == "stale") project.Revision++;
        await db.SaveChangesAsync();
        var service = new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System));
        await Assert.ThrowsAnyAsync<Exception>(() => service.PrepareDeliveryAsync(manager.OrganizationId, manager.AgentInstallationId!.Value, request, default));
        Assert.Empty(db.WorkBoards); Assert.Empty(db.ProjectParticipants); Assert.Empty(db.AgentPlatformEventOutbox);
    }
    [Fact]
    public void Published_manager_profile_has_a_valid_bounded_execution_graph()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "CSweet.Agent.Producer.VideoGame"))) root = root.Parent;
        Assert.NotNull(root);
        var bytes = File.ReadAllBytes(Path.Combine(root.FullName, "CSweet.Agent.Producer.VideoGame/profiles/video-game-manager-brief.v2.json"));
        var profile = WorkstreamProfileDefinitionValidator.Validate(new PluginWorkstreamProfileContribution { Key = "video-game-manager-brief.v1", Version = 2, DefinitionResource = "profiles/video-game-manager-brief.v2.json" }, bytes);
        Assert.Equal(2, profile.Version);
        using var doc = JsonDocument.Parse(profile.DefinitionJson);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var graph = doc.RootElement.GetProperty("orchestration").Deserialize<CSweet.WorkManagement.Contracts.WorkOrchestrationProfileTemplate>(options)!;
        var workflow = doc.RootElement.GetProperty("boardWorkflow").Deserialize<CSweet.WorkManagement.Contracts.WorkBoardWorkflowTemplate>(options)!;
        var columns = workflow.Columns.ToDictionary(x => x.Key, _ => Guid.NewGuid());
        Assert.Empty(WorkOrchestrationPolicyValidator.Validate(graph.InitialStageKey, graph.MergeMode, graph.Concurrency,
            graph.Stages.Select(x => new CSweet.WorkManagement.Contracts.WorkOrchestrationStageDefinition(x.Key, x.Name, x.StageType,
                x.ColumnKey is null ? null : columns[x.ColumnKey], x.Instructions, x.InputSchemaJson, x.OutputSchemaJson,
                x.TimeoutSeconds, x.ConcurrencyLimit, x.RetryPolicy, x.PlatformAction, x.IsSuccessfulTerminal)).ToArray(), graph.Transitions, columns.Values.ToHashSet()));
        var stages = doc.RootElement.GetProperty("orchestration").GetProperty("stages");
        Assert.Contains(stages.EnumerateArray(), x => x.GetProperty("key").GetString() == "quality");
        Assert.Contains(stages.EnumerateArray(), x => x.TryGetProperty("platformAction", out var action) && action.ValueKind == JsonValueKind.String);
    }
}
