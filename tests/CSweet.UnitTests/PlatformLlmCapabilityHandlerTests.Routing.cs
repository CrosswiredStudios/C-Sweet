using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    [Fact]
    public async Task ApprovedFallbackServesRequestWhenPrimaryIsUnavailableBeforeOutput()
    {
        var (db, session, primary, fallback) = await RoutingFixtureAsync();
        await using var _ = db;
        var standby = new StreamingChatClient();
        var factory = new RoutingProviderFactory(new() { [primary] = new FailingChatClient(HttpStatusCode.ServiceUnavailable), [fallback] = standby });
        var results = await RunAsync(db, factory, session, primary);
        Assert.All(results, value => Assert.True(value.Succeeded, value.Error));
        Assert.NotNull(standby.ReceivedOptions);
        var logs = await db.AgentRunLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal("Failed", logs.Single(x => x.Model == "primary-model").Status);
        using var settings = JsonDocument.Parse(logs.Single(x => x.Model == "standby-model").InferenceSettingsJson!);
        Assert.Equal(1, settings.RootElement.GetProperty("routeAttempt").GetInt32());
    }

    [Fact]
    public async Task FallbackIsNeverUsedAfterOutputOrForNonTransientFailures()
    {
        var (db, session, primary, fallback) = await RoutingFixtureAsync();
        await using var _ = db;
        var standby = new StreamingChatClient();
        // A partial answer already reached the agent: it must not be spliced with another model's output.
        var partial = await RunAsync(db, new RoutingProviderFactory(new() { [primary] = new FailingChatClient(HttpStatusCode.ServiceUnavailable, emitFirst: true), [fallback] = standby }), session, primary);
        Assert.True(partial[0].Succeeded); Assert.False(partial[^1].Succeeded);
        // A request the provider rejects would be rejected by any target; it is not an outage.
        var rejected = await RunAsync(db, new RoutingProviderFactory(new() { [primary] = new FailingChatClient(HttpStatusCode.BadRequest), [fallback] = standby }), session, primary);
        Assert.False(Assert.Single(rejected).Succeeded);
        Assert.Null(standby.ReceivedOptions);
        Assert.DoesNotContain(await db.AgentRunLogs.Select(x => x.Model).ToListAsync(), model => model == "standby-model");
    }

    [Fact]
    public async Task ExhaustedTaskBudgetIsExplicitNonRetryableAndDoesNotFallBack()
    {
        var (db, session, primary, fallback) = await RoutingFixtureAsync(policy => policy with { TaskTokenLimit = 10 });
        await using var _ = db;
        var standby = new StreamingChatClient();
        var results = await RunAsync(db, new RoutingProviderFactory(new() { [primary] = new StreamingChatClient(), [fallback] = standby }), session, primary);
        var failure = Assert.Single(results);
        Assert.False(failure.Succeeded);
        Assert.Equal("harness.budget_exhausted", failure.FailureCode);
        Assert.False(failure.Retryable);
        Assert.Contains("remaining", failure.Error);
        Assert.Null(standby.ReceivedOptions);
    }

    [Fact]
    public async Task AgentReadsItsOwnRouteAndRemainingBudgetWithoutAGrant()
    {
        var (db, session, primary, _) = await RoutingFixtureAsync(policy => policy with { TaskTokenLimit = 5000 });
        await using var __ = db;
        var handler = new HarnessStatusCapabilityHandler(db);
        var results = new List<CapabilityResult>();
        await foreach (var value in handler.HandleAsync(session, new RequestCapability { RequestId = "status", Capability = HarnessStatusCapabilityHandler.Capability }, default))
            results.Add(value);
        var status = JsonSerializer.Deserialize<HarnessStatus>(Assert.Single(results).Payload.Span, HarnessWorkspaceService.Json)!;
        Assert.Equal(("Default", "Primary", "primary-model", 1), (status.Stage, status.Provider, status.Model, status.FallbackCount));
        Assert.Equal(5000, status.TaskTokensRemaining);
        // Another installation's session cannot read this policy through its own identity.
        var stranger = session with { InstallationId = Guid.NewGuid().ToString("D") };
        results.Clear();
        await foreach (var value in handler.HandleAsync(stranger, new RequestCapability { RequestId = "status", Capability = HarnessStatusCapabilityHandler.Capability }, default))
            results.Add(value);
        Assert.False(Assert.Single(results).Succeeded);
    }

    [Fact]
    public void FallbackTargetsAreBoundedDistinctAndPricedUnderACostCeiling()
    {
        var provider = Guid.NewGuid();
        var route = new HarnessRoute("Default", provider, "a", 1, 1);
        Assert.Throws<ArgumentException>(() => HarnessWorkspaceService.Validate(new(Routes: [route with { Fallbacks = [new(provider, "a")] }])));
        Assert.Throws<ArgumentException>(() => HarnessWorkspaceService.Validate(new(Routes: [route with { Fallbacks = [new(provider, "b"), new(provider, "c"), new(provider, "d"), new(provider, "e")] }])));
        Assert.Throws<ArgumentException>(() => HarnessWorkspaceService.Validate(new(TaskCostLimit: 1, Routes: [route with { Fallbacks = [new(provider, "b")] }])));
        HarnessWorkspaceService.Validate(new(TaskCostLimit: 1, Routes: [route with { Fallbacks = [new(Guid.NewGuid(), "b", 2, 2)] }]));
        Assert.True(HarnessRuntime.Approves(new(Routes: [route with { Fallbacks = [new(provider, "b")] }]), provider, "b"));
        Assert.False(HarnessRuntime.Approves(new(Routes: [route]), provider, "b"));
    }

    [Fact]
    public void SpilledResultsPointAtTheModelVisibleRetrievalTool()
    {
        var messages = new[] { new ChatMessage(ChatRole.Tool, [new FunctionResultContent("big", new string('x', 20000))]),
            new ChatMessage(ChatRole.User, "next"), new ChatMessage(ChatRole.User, "latest") };
        var change = Assert.Single(HarnessContextOptimizer.Spill(messages, Guid.NewGuid()));
        var text = (string)((FunctionResultContent)messages[0].Contents[0]).Result!;
        Assert.Contains(HarnessContextOptimizer.ContextToolName, text);
        Assert.DoesNotContain(HarnessContextCapabilityHandler.Capability, text);
        Assert.True(change.RemainingCharacters < change.OriginalCharacters);
    }

    private static async Task<(CSweetDbContext Db, AgentSession Session, Guid Primary, Guid Fallback)> RoutingFixtureAsync(Func<HarnessPolicy, HarnessPolicy>? configure = null)
    {
        var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var primary = await AddProviderAsync(db);
        var fallback = await AddProviderAsync(db);
        (await db.LlmProviderProfiles.SingleAsync(x => x.Id == primary)).Name = "Primary";
        var installation = Guid.NewGuid();
        var session = new AgentSession(Guid.NewGuid().ToString("N"), "test-agent", installation.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>([PlatformCapabilities.LlmChatStream], StringComparer.Ordinal), 1));
        await SeedDispatchAuthorityAsync(db, session);
        var policy = new HarnessPolicy(1, Routes: [new("Default", primary, "primary-model", Fallbacks: [new(fallback, "standby-model")])]);
        policy = configure?.Invoke(policy) ?? policy;
        db.AgentHarnessPolicies.Add(new() { Id = installation, Revision = 1, PolicyJson = JsonSerializer.Serialize(policy, HarnessWorkspaceService.Json) });
        await db.SaveChangesAsync();
        return (db, session, primary, fallback);
    }

    private static async Task<List<CapabilityResult>> RunAsync(CSweetDbContext db, ILlmProviderFactory factory, AgentSession session, Guid provider)
    {
        var handler = new PlatformLlmCapabilityHandler(db, factory, new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString("N"), Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(new { providerProfileId = provider, model = "caller-model", messages = new[] { new { role = "user", text = "Review it" } } }) };
        var results = new List<CapabilityResult>();
        await foreach (var value in handler.StreamAsync(session, request, default)) results.Add(value);
        return results;
    }

    private sealed class RoutingProviderFactory(Dictionary<Guid, IChatClient> clients) : ILlmProviderFactory
    {
        public Task<IChatClient> CreateChatClientAsync(Guid providerProfileId, CancellationToken cancellationToken = default) => Task.FromResult(clients[providerProfileId]);
        public Task<IChatClient> CreateChatClientAsync(Guid providerProfileId, string? model, CancellationToken cancellationToken = default) => Task.FromResult(clients[providerProfileId]);
    }

    private sealed class FailingChatClient(HttpStatusCode status, bool emitFirst = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("provider failure", null, status);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (emitFirst) yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Partial ")]);
            throw new HttpRequestException("provider failure", null, status);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
