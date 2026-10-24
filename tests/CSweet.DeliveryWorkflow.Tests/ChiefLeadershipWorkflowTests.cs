using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Orchestration;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;
using CSweet.UnitTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.DeliveryWorkflow.Tests;

// These integration checks join the shipping Chief entrypoint to the real broker and
// platform services. They supplement, rather than replace, installed/UI/model acceptance.
public sealed class ChiefLeadershipWorkflowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("owner")]
    [InlineData("external")]
    [InlineData("defer")]
    [InlineData("hire")]
    [InlineData("creative-hire")]
    public Task NativeFocusThroughCoverageDecisionRecoversAcrossRestarts(string outcome) => JourneyAsync(outcome, false);

    [ChiefPostgresFact]
    public Task NativeFocusAndDecisionUseRealPostgresTransactions() => JourneyAsync("owner", true);

    [ChiefPostgresFact]
    public Task HiringDeliveryFulfillmentAndContinuationUseRealPostgresTransactions() => JourneyAsync("hire", true);

    [ChiefPostgresFact]
    public Task CreativeFocusStartsWithoutRolloutConfigurationAndTracksDirectorFulfillment() => JourneyAsync("creative-hire", true);

    private static async Task JourneyAsync(string outcome, bool postgres)
    {
        var creative = outcome == "creative-hire";
        if (creative) outcome = "hire";
        var domain = creative ? "creative-product" : "product";
        var role = creative ? "creative-director" : "product-manager";
        var title = creative ? "Creative Director" : "Product Manager";
        var builder = new DbContextOptionsBuilder<CSweetDbContext>();
        if (postgres)
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES"))
                { Database = "chief_workflow_" + Guid.NewGuid().ToString("N") };
            Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
            connection.Pooling = false;
            builder.UseNpgsql(connection.ConnectionString);
        }
        else builder.UseInMemoryDatabase(Guid.NewGuid().ToString());
        await using var db = new CSweetDbContext(builder.Options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var f = await UserActionServiceTests.ChiefAcceptanceFixtureAsync(db);
            var loseCoverageAcknowledgment = outcome == "hire";
            var audit = new TestAuditEventWriter();
            var hiring = new HiringService(db, new OrganizationUserService(db, audit), audit);
            var actions = new UserActionService(db, [new HiringMarketplaceUserActionWorkflowResolver()]);
            var hub = new CommunicationHubService(db, audit, new ChatTurnService(db));
            var permissions = new HashSet<string>(ChiefLeadershipCapabilities.All);
            permissions.UnionWith([PlatformCapabilities.AgentOperatingStateRead, PlatformCapabilities.AgentOperatingStateWrite,
                Wire.PersonalTodoCapabilities.Read, Wire.PersonalTodoCapabilities.Add,
                ChiefOfStaffProfile.ReadCommunicationCapability, ChiefOfStaffProfile.SendCommunicationMessageCapability,
                PlatformCapabilities.UserActionSuggest]);
            var session = new AgentSession("test", "chief", f.Installation.ToString(), f.Organization.ToString(),
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), permissions, Revision: 1));
            async Task<TResponse> Broker<TRequest, TResponse>(string capability, TRequest input, CancellationToken token)
            {
                if (loseCoverageAcknowledgment && input is AgentOperatingStateWriteRequest { Status: "Acknowledged" })
                    throw new IOException("Simulated interruption after real hiring notification effects, before acknowledgment.");
                // A new service and broker for each request exercises durable reads, not agent memory.
                IPlatformCapabilityHandler[] handlers = [new ChiefLeadershipCapabilityHandler(UserActionServiceTests.ChiefAcceptanceService(db)),
                    new PluginOperationsCapabilityHandler(db, audit),
                    new PersonalTodoCapabilityHandler(db, new PersonalTodoService(db, TimeProvider.System)),
                    new CommunicationHubCapabilityHandler(db, hub, userActions: actions)];
                var handler = handlers.Single(x => x.CanHandle(capability));
                await foreach (var result in handler.HandleAsync(session, new RequestCapability {
                    RequestId = Guid.NewGuid().ToString(), Capability = capability,
                    Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(input, Json)) }, token))
                {
                    if (!result.Succeeded)
                    {
                        var error = JsonSerializer.Deserialize<PlatformCapabilityError>(result.Payload.Span, Json)!;
                        throw new PlatformCapabilityException(capability, error.Code, error.Message);
                    }
                    return JsonSerializer.Deserialize<TResponse>(result.Payload.Span, Json)!;
                }
                throw new InvalidOperationException("Missing broker response.");
            }
            var runtime = new AgentTestRuntime()
                .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read,
                    (r, t) => Broker<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read, r, t))
                .RegisterCapability<PresentChiefFocusRequest, PresentedChiefFocus>(ChiefLeadershipCapabilities.Present,
                    (r, t) => Broker<PresentChiefFocusRequest, PresentedChiefFocus>(ChiefLeadershipCapabilities.Present, r, t))
                .RegisterCapability<RecordChiefAssessmentRequest, ChiefCoverageAssessment>(ChiefLeadershipCapabilities.Assess,
                    (r, t) => Broker<RecordChiefAssessmentRequest, ChiefCoverageAssessment>(ChiefLeadershipCapabilities.Assess, r, t))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, t) => Broker<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, r, t))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, t) => Broker<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, r, t))
                .RegisterCapability<JsonElement, Wire.PersonalTodoDirectory>(Wire.PersonalTodoCapabilities.Read,
                    (r, t) => Broker<JsonElement, Wire.PersonalTodoDirectory>(Wire.PersonalTodoCapabilities.Read, r, t))
                .RegisterCapability<Wire.AddPersonalTodoItemRequest, Wire.PersonalTodoItem>(Wire.PersonalTodoCapabilities.Add,
                    (r, t) => Broker<Wire.AddPersonalTodoItemRequest, Wire.PersonalTodoItem>(Wire.PersonalTodoCapabilities.Add, r, t))
                .RegisterCapability<JsonElement, JsonElement>(ChiefOfStaffProfile.ReadCommunicationCapability,
                    (r, t) => Broker<JsonElement, JsonElement>(ChiefOfStaffProfile.ReadCommunicationCapability, r, t))
                .RegisterCapability<SendCommunicationMessageRequest, JsonElement>(ChiefOfStaffProfile.SendCommunicationMessageCapability,
                    (r, t) => Broker<SendCommunicationMessageRequest, JsonElement>(ChiefOfStaffProfile.SendCommunicationMessageCapability, r, t))
                .RegisterCapability<SuggestUserActionRequest, SuggestedUserActionResponse>(PlatformCapabilities.UserActionSuggest,
                    (r, t) => Broker<SuggestUserActionRequest, SuggestedUserActionResponse>(PlatformCapabilities.UserActionSuggest, r, t))
                .RegisterCapability<JsonElement, HiringBacklogResponse>(PlatformCapabilities.HiringRecommendationList, async (_, t) =>
                    new(JsonSerializer.Deserialize<HiringRecommendationResponse[]>(JsonSerializer.Serialize(
                        await hiring.ListRecommendationsForInstallationAsync(f.Organization, f.Installation, t), Json), Json)!));
            var context = runtime.CreateContext(f.Organization.ToString(), f.Installation.ToString(),
                new(f.Chief.ToString(), "Chief", null, null, null, [], null, null, null));
            var question = new ChiefCoverageResult("NeedsCoverage", "Unresolved", null,
                "Current platform assignments do not establish product accountability.", "Question", [], Question: "Who owns product decisions?");
            var model = new Model(question);
            if (outcome == "hire") model.Result = new("NeedsCoverage", "Unresolved", null, "Verified product accountability gap.", "Hire", [],
                RoleKey: role, RoleTitle: title, RoleObjective: "Own creative and product outcomes.");
            ChiefOfStaffAgent Agent() => new(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator());
            Task Wake() => Agent().HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), ChiefLeadershipCapabilities.Changed,
                JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, "acceptance"), context, default);

            var focus = await context.Platform.ChiefLeadership.PresentAsync(new(creative ? "game-studio" : "general", 1, f.Conversation, f.Message,
                [new(domain, title, "Product accountability", 1), new("research", "Research", "Validate assumptions", 2)], 0, "present"));
            var answer = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation,
                focus.DecisionId!.Value, f.Owner, new(domain, null, "native-focus"));
            Assert.True(answer.Succeeded, answer.Message);
            await new ChatTurnService(db).CompleteAsync(answer.Turn!.Id, f.Message, false);
            db.ChangeTracker.Clear();
            if (creative)
            {
                var ready = await context.Platform.ChiefLeadership.ReadAsync();
                Assert.True(ready.ProactiveEnabled); Assert.Null(ready.ConfigurationBlock);
                Assert.Equal(domain, ready.Focus!.DomainId); Assert.NotNull(ready.NextAssessmentId);
                Assert.Equal(0, model.Calls); Assert.Empty(await db.WorkforcePlans.ToListAsync());
            }
            // Miss the callback entirely: the next wake recovers the immutable native answer.
            if (outcome == "hire")
            {
                await Assert.ThrowsAsync<IOException>(Wake);
                loseCoverageAcknowledgment = false;
                db.ChangeTracker.Clear();
            }
            await Wake();
            var waiting = await context.Platform.ChiefLeadership.ReadAsync();
            var product = waiting.Assessments.Single(x => x.DomainId == domain);
            Assert.Equal(outcome == "hire" ? "WaitingHire" : "WaitingDecision", product.ExecutionStatus);
            Assert.Null(waiting.NextAssessmentId);
            Assert.Equal(outcome == "hire" ? 1 : 0, await db.WorkforcePlans.CountAsync());
            Assert.Equal(WorkTaskStatus.Running, (await db.CoreWorkTasks.SingleAsync(x => x.Id == product.WorkItemId)).Status);
            await Wake(); // duplicate/out-of-order hint while waiting has no new model/effect
            Assert.Equal(1, model.Calls);
            if (outcome == "hire")
            {
                Assert.Single(await db.SuggestedUserActions.ToListAsync());
                Assert.Equal(role, (await db.WorkforcePlans.SingleAsync()).RoleKey);
                await hiring.ResolveRecommendationAsync(f.Organization, f.Installation, new(product.RecommendationId!.Value, f.Hire, "fulfill"));
                db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
                    PositionKey = role, StartsAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            else
            {
                var decision = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation,
                    product.DecisionId!.Value, f.Owner, new(outcome, null, "coverage-choice"));
                Assert.True(decision.Succeeded, decision.Message);
                await new ChatTurnService(db).CompleteAsync(decision.Turn!.Id, f.Message, false);
            }
            db.ChangeTracker.Clear();
            // Interrupt after the owner's commit but before the agent callback.
            if (outcome == "hire")
            {
                model.Result = new("AdequatelyCovered", "Combined", f.Hire, "The assigned manager owns product outcomes with capacity for this scope.", "None", [f.Hire]);
                await Wake();
            }
            model.Result = new("NotApplicable", "Unresolved", null, "No additional research responsibility in the current scope.", "None", []);
            await Wake(); await Wake();
            var final = await context.Platform.ChiefLeadership.ReadAsync();
            var finalProduct = final.Assessments.Single(x => x.Id == product.Id);
            Assert.Equal(outcome == "external" ? "Blocked" : "Complete", finalProduct.ExecutionStatus);
            Assert.Equal(outcome == "external" ? WorkTaskStatus.Blocked : WorkTaskStatus.Completed,
                (await db.CoreWorkTasks.SingleAsync(x => x.Id == product.WorkItemId)).Status);
            Assert.Equal(outcome == "external" ? 1 : outcome == "hire" ? 3 : 2, model.Calls);
            Assert.Equal(outcome == "hire" ? 1 : 0, await db.WorkforcePlans.CountAsync());
            Assert.Equal(outcome == "hire" ? 1 : 2, await db.ChiefFocusPrompts.CountAsync());
            Assert.Single(await db.ChiefFocusRecords.ToListAsync());
            Assert.Equal(2, await db.CoreWorkTasks.CountAsync(x => x.CorrelationId!.StartsWith("chief-assessment:")));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class Model(ChiefCoverageResult result) : IChatClient, IAgentLlmClientFactory
    {
        public ChiefCoverageResult Result = result;
        public int Calls;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(Result, Json)))); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
    private sealed class Orchestrator : IChiefOfStaffOrchestrator
    {
        public Task<ChiefOperatingContext> AssembleContextAsync(AgentRuntimeContext context, CancellationToken token) => Task.FromResult(new ChiefOperatingContext(null, null, null, null, null, null, []));
        public string BuildGroundedPrompt(string prompt, string capability, ChiefOperatingContext context, AgentSettings settings) => prompt;
        public Task CaptureExplicitFactsAsync(IChatClient model, AssistantCapabilityInput input, ChiefOperatingContext context, AgentRuntimeContext runtime, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ChiefPostgresFactAttribute : FactAttribute
    {
        public ChiefPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES")))
                Skip = "Set CSWEET_HIRING_TEST_POSTGRES to a disposable loopback PostgreSQL test server.";
        }
    }
}
