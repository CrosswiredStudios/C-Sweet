using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.UnitTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.DeliveryWorkflow.Tests;

public sealed partial class ChiefLeadershipWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDirectorCoverageChecksWorkloadAndSupportsAnAuthenticatedNamedConfirmation(bool conflict)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var f = await UserActionServiceTests.ChiefAcceptanceFixtureAsync(db);
        var marcus = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Hire); marcus.DisplayName = "Marcus Vale";
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
            PositionKey = "game-director", StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        db.Workstreams.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, Name = "Current game", Outcome = "Deliver the demo",
            AccountableManagerOrganizationUserId = f.Hire, Status = conflict ? WorkstreamStatus.Blocked : WorkstreamStatus.Active });
        await db.SaveChangesAsync();
        var service = UserActionServiceTests.ChiefAcceptanceService(db);
        var focus = await service.PresentAsync(f.Organization, f.Installation, new("game-studio", 1, f.Conversation, f.Message,
            [new("creative-product", "Creative & Product", "Portfolio accountability", 1), new("research", "Research", "Research leadership", 2)], 0, "focus"));
        var answer = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation,
            focus.DecisionId!.Value, f.Owner, new("creative-product", null, "choose-focus"));
        Assert.True(answer.Succeeded, answer.Message);
        await new ChatTurnService(db).CompleteAsync(answer.Turn!.Id, f.Message, false);
        var session = new AgentSession("test", "chief", f.Installation.ToString(), f.Organization.ToString(), Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(), new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), ChiefLeadershipCapabilities.All, Revision: 1));
        async Task<TResponse> Broker<TRequest, TResponse>(string capability, TRequest request, CancellationToken token)
        {
            var handler = new ChiefLeadershipCapabilityHandler(UserActionServiceTests.ChiefAcceptanceService(db));
            await foreach (var result in handler.HandleAsync(session, new() { RequestId = "existing-director", Capability = capability,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(request, Json)) }, token))
            {
                Assert.True(result.Succeeded, result.Error);
                return JsonSerializer.Deserialize<TResponse>(result.Payload.Span, Json)!;
            }
            throw new InvalidOperationException("Missing broker response.");
        }
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read,
                (r, t) => Broker<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read, r, t))
            .RegisterCapability<RecordChiefAssessmentRequest, ChiefCoverageAssessment>(ChiefLeadershipCapabilities.Assess,
                (r, t) => Broker<RecordChiefAssessmentRequest, ChiefCoverageAssessment>(ChiefLeadershipCapabilities.Assess, r, t));
        var model = new Model(new("AtRisk", "Agent", f.Hire, "The current assigned project is blocked and needs an ownership decision.",
            "Question", [f.Hire], Question: "Should Marcus remain accountable while the blocked project is resolved?"));
        async Task Wake()
        {
            var agent = new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance,
                new Orchestrator(() => OperatingFixture(db, f.Organization)));
            Assert.True((await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
                    ["llmModel"] = JsonSerializer.SerializeToElement("fixture-model"),
                    ["businessOperatingProfile"] = JsonSerializer.SerializeToElement("game-studio") }))).Succeeded);
            await runtime.DeliverEventAsync(agent, ChiefLeadershipCapabilities.Changed, new { });
        }
        await Wake();
        var state = await service.ReadAsync(f.Organization, f.Installation, new());
        var coverage = state.Assessments.Single(x => x.DomainId == "creative-product");
        if (conflict)
        {
            Assert.Equal("WaitingDecision", coverage.ExecutionStatus);
            var decision = await db.ExecutiveDecisions.SingleAsync(x => x.Id == coverage.DecisionId);
            using var options = JsonDocument.Parse(decision.OptionsJson);
            var confirm = options.RootElement.GetProperty("options")[0];
            Assert.Equal("Yes, Marcus Vale is accountable", confirm.GetProperty("label").GetString());
            var confirmed = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation,
                decision.Id, f.Owner, new(confirm.GetProperty("id").GetString(), null, "confirm-marcus"));
            Assert.True(confirmed.Succeeded, confirmed.Message);
            await new ChatTurnService(db).CompleteAsync(confirmed.Turn!.Id, f.Message, false);
        }
        else Assert.Null(coverage.DecisionId);
        db.ChangeTracker.Clear(); await Wake(); await Wake();
        state = await service.ReadAsync(f.Organization, f.Installation, new());
        coverage = state.Assessments.Single(x => x.Id == coverage.Id);
        Assert.Equal("Complete", coverage.ExecutionStatus); Assert.Equal("AdequatelyCovered", coverage.CoverageStatus);
        Assert.Equal(f.Hire, coverage.AccountableOwnerId); Assert.Equal(conflict ? 1 : 0, model.Calls);
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
        Assert.Single(await db.ChiefFocusPrompts.Where(x => x.Purpose == "NextFocus").ToListAsync());
    }
}
