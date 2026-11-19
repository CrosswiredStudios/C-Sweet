extern alias Director;
extern alias Producer;

using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Manager = Director::CSweet.Agent.Director.VideoGame;
using Production = Producer::CSweet.Agent.Producer.VideoGame;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Planning_question_routes_to_the_team_or_to_one_owner_according_to_the_selected_action(bool changeScope)
    {
        const string requirement = "Keep distinct feedback for a power-up catch.";
        const string direction = "Gabriel owns the package label check. Victor, scope the catch feedback and have Adrian estimate the setup before committing sprint capacity.";
        var model = new FirstSprintModel(false) { Pitch = requirement + " Label the internal build Prism Break PoC.",
            Outcome = "The player sees the catch feedback." };
        model.Stack = model.Stack with { Setup = "Open the scene and verify the catch feedback." };
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var marcus = await platform.AddProjectDirectorAsync();
        var digest = FreshTeamSprintPlatform.Digest(model.Pitch);
        var brief = await platform.AddProductionBriefAsync(model.Pitch, marcus);
        var handoff = new Production.ProducerAcceptedHandoff(platform.Project, brief.Document, brief.Revision, digest,
            digest, Guid.NewGuid(), platform.PackageId, 1, DateTimeOffset.UtcNow);
        var team = new PlanningTeam(platform, model, handoff);
        await team.StartHostsAsync();
        var producer = team.ConnectPlanningDecisions();
        var question = changeScope ? "Should we remove distinct catch feedback from the accepted game?"
            : "Can delivery own the package label and who can size the catch feedback?";
        // Exercise the Producer's actual durable request path, with C-Sweet accepting and deduplicating its MCP call.
        var ensure = typeof(Production.SpecialistAgent).GetMethod("EnsurePlanningDecisionsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        async Task<DecisionRecord> RequestAsync() => Assert.Single(await (Task<IReadOnlyList<DecisionRecord>>)ensure.Invoke(null,
            [platform.Project, platform.Board, new[] { question }, handoff, producer, CancellationToken.None])!);
        var decision = await RequestAsync();
        Assert.Null(decision.TypeData);
        Assert.Equal(question, decision.Summary);
        Assert.Equal(decision.Id, (await RequestAsync()).Id);
        var runtime = new AgentTestRuntime();
        var state = new OperatingStateFixture(runtime);
        state.Seed($"{Manager.VideoGameDirectorAgent.StateKey}:workstream:{platform.Project:N}", new Manager.GameDirectorOperatingState {
            WorkstreamId = platform.Project, BoardId = platform.Board, TeamId = platform.Team,
            AcceptedVision = new(1, digest, model.Pitch, platform.PitchId, platform.RevisionId, digest, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) });
        platform.Connect(runtime, marcus);
        platform.ConnectDecisions(runtime, marcus);
        runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (r, _) => {
            Assert.Equal(platform.Team, r.TeamId);
            return Task.FromResult(new TeamRosterV2Response(platform.Roster, platform.Project));
        });
        runtime.RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([])));
        runtime.RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([]));
        new FreshTeamPlanningQueue(marcus.OrganizationUserId).Connect(runtime, marcus.OrganizationUserId);
        runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (input, _) => {
            var check = input.GetRawText().Contains("selected immediate action");
            if (!check) Assert.Contains("Adrian", input.GetRawText());
            object reply = check ? new { change = changeScope ? new { acceptedRequirement = requirement, proposedChange = "Remove the distinct catch feedback." } : null }
                : new { optionId = "provide-direction", rationale = changeScope ? "Remove the distinct feedback from the deliverable." : direction };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(reply) }));
        });
        var identity = new AgentIdentity(marcus.OrganizationUserId.ToString(), "Marcus", null, marcus.Role, null, [], null, null, null);
        var notice = new GenericResourceEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, new(platform.Organization, platform.Project, null, null, null, null, null, Guid.NewGuid(), null, null),
            "Decision", decision.Id, decision.Revision, decision.TypeKey, "requested", JsonSerializer.SerializeToElement(new {}));
        var eventId = Guid.NewGuid();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var host = new AgentTestHost(new Manager.VideoGameDirectorAgent(), runtime, identity, platform.Organization.ToString(), marcus.AgentInstallationId.ToString());
            var configured = await host.ExecuteCapabilityAsync(AgentConfigurationCapabilities.Update, new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("scripted") }));
            Assert.True(configured.Succeeded, configured.Error);
            var result = await host.DeliverEventAsync(WorkstreamEventNames.DecisionRequestedV1, notice, eventId);
            Assert.True(result.Succeeded, result.Error);
        }
        if (changeScope)
        {
            var card = Assert.Single(await platform.Db.ExecutiveDecisions.ToListAsync());
            Assert.Contains("Remove the distinct catch feedback.", card.Prompt);
            Assert.Empty(await platform.Db.WorkSprints.ToListAsync());
            var owner = await platform.Db.CoreOrganizationUsers.SingleAsync(u => u.DisplayName == "Business owner");
            var service = new CSweet.Infrastructure.Communications.ExecutiveDecisionService(platform.Db,
                new CSweet.Infrastructure.Core.ChatTurnService(platform.Db));
            var pending = Assert.Single(await service.ListPendingForUserAsync(platform.Organization, owner.Id));
            var change = Assert.Single(pending.Decision.Options, o => o.Id == "approve-change");
            Assert.Equal("Remove the distinct catch feedback.", change.Description);
            Assert.False(string.IsNullOrWhiteSpace(change.Impact));
            var answer = await service.AnswerAsync(platform.Organization, card.ConversationId, card.Id, owner.Id,
                new("keep-requirement", null, "owner-keeps-the-brief"));
            Assert.True(answer.Succeeded, answer.Message);
            var source = await platform.Db.WorkstreamDecisions.SingleAsync(r => r.AuthorityRuleKey == "material-strategy-change");
            var late = new DecisionRequest(platform.Project, decision.TypeKey, question, "work-planning", decision.Options,
                decision.RecommendedOptionId, decision.Evidence, null, "Recheck staffing", source.Id, "stale-recovery");
            var rejection = await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() => runtime.CreateContext().Platform.RequestDecisionAsync(late));
            Assert.Contains("answer must be preserved", rejection.Message);
            Assert.Equal(owner.Id, source.DecidedByOrganizationUserId);
            Assert.Null(source.SupersededByDecisionId);
            return;
        }
        Assert.Equal(direction, (await RequestAsync()).Rationale);
        await team.PlanAsync();
        Assert.Contains(team.TechnicalPlanningRequests, message => message.Contains(direction, StringComparison.Ordinal));
        Assert.Equal(1, model.Estimates);
        var sprint = await platform.Db.WorkSprints.SingleAsync();
        Assert.Equal(1, sprint.Sequence);
        Assert.Equal(WorkSprintStatus.Active, sprint.Status);
        var task = await platform.Db.CoreWorkTasks.SingleAsync(t => t.SprintId == sprint.Id && t.IsExecutable);
        Assert.Equal(platform.Engineer.OrganizationUserId, task.AccountableOrganizationUserId);
        Assert.Equal(2m, task.EstimatePoints);
        var lease = await platform.ClaimAsync();
        Assert.NotNull(lease);
        Assert.Equal(task.Id, lease.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!.ItemId);
        Assert.Equal(1, lease.Attempt);
        await team.RestartAndRedeliverAsync();
        Assert.Null(await platform.ClaimAsync());
        Assert.Single(await platform.Db.WorkstreamDecisions.ToListAsync());
        Assert.Empty(await platform.Db.ExecutiveDecisions.ToListAsync());
        Assert.Empty(await platform.Db.WorkItemApprovals.ToListAsync());
    }

    internal sealed partial class PlanningTeam
    {
        public IEnumerable<string> TechnicalPlanningRequests => sessions.Values.Where(s => s.Target == platform.Victor).Select(s => s.Turns[0].Content);

        public AgentRuntimeContext ConnectPlanningDecisions()
        {
            var runtime = platforms[platform.Gabriel.OrganizationUserId];
            platform.ConnectDecisions(runtime, platform.Gabriel);
            return runtime.CreateContext();
        }
    }
}
