using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    [Fact]
    public async Task SavedRouteAndSkillReachProviderAndRevisionChangeStopsNextDispatch()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var provider = await AddProviderAsync(db);
        var installation = Guid.NewGuid();
        var session = new AgentSession(Guid.NewGuid().ToString("N"), "test-agent", installation.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>([PlatformCapabilities.LlmChatStream], StringComparer.Ordinal), 1));
        await SeedDispatchAuthorityAsync(db, session);
        var skill = new BusinessSkillRevision { Id = Guid.NewGuid(), BusinessId = session.BusinessId, Name = "Evidence", Instructions = "Require source evidence for every conclusion", Version = 1, Digest = "testdigest" };
        db.BusinessSkillRevisions.Add(skill);
        db.AgentHarnessPolicies.Add(new() { Id = installation, Revision = 1, PolicyJson = JsonSerializer.Serialize(new HarnessPolicy(1, Routes: [new("Default", provider, "approved-specialist")], SkillRevisionIds: [skill.Id]), HarnessWorkspaceService.Json) });
        await db.SaveChangesAsync();
        var client = new StreamingChatClient();
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(client), new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var request = new RequestCapability { RequestId = "harness-route", Capability = PlatformCapabilities.LlmChatStream, Payload = JsonPayload.From(new { providerProfileId = provider, model = "caller-model", messages = new[] { new { role = "user", text = "Review it" } } }) };
        var results = new List<CapabilityResult>();
        await foreach (var value in handler.StreamAsync(session, request, default)) results.Add(value);
        Assert.All(results, value => Assert.True(value.Succeeded, value.Error));
        Assert.Contains(skill.Instructions, client.ReceivedOptions!.Instructions);
        var log = await db.AgentRunLogs.SingleAsync();
        Assert.Equal("approved-specialist", log.Model); Assert.Contains(skill.Digest, log.InferenceSettingsJson);
        skill.Enabled = false; await db.SaveChangesAsync(); results.Clear();
        await foreach (var value in handler.StreamAsync(session, request, default)) results.Add(value);
        Assert.False(Assert.Single(results).Succeeded); Assert.Equal(1, await db.AgentRunLogs.CountAsync());
    }
    [Fact]
    public async Task SpilledContextIsBoundedAndCannotCrossWorkOrRuntimeScopes()
    {
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, protection);
        var installation = Guid.NewGuid();
        var session = new AgentSession(Guid.NewGuid().ToString("N"), "test-agent", installation.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>([PlatformCapabilities.LlmChatStream, HarnessContextCapabilityHandler.Capability], StringComparer.Ordinal), 1));
        await SeedDispatchAuthorityAsync(db, session);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = session.BusinessId, AgentInstallationId = installation, Status = AgentWorkStatus.Leased, DeadlineAt = DateTimeOffset.UtcNow.AddHours(1) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1, RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        db.AddRange(work, attempt);
        var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(session.BusinessId), AgentInstallationId = installation, AgentWorkItemId = work.Id, AgentWorkAttemptId = attempt.Id, StartedAt = DateTimeOffset.UtcNow, Status = "Running",
            RequestEvidenceJson = JsonSerializer.Serialize(new { contextScope = new { runtimeId = session.RuntimeInstanceId, tickId = session.TickId }, request = new { messages = new[] { new { role = "tool", contents = new[] { new { kind = "function_result", callId = "large-result", result = new string('x', 20000) } } } } } }) };
        db.AgentRunLogs.Add(run); await db.SaveChangesAsync();
        var handler = new HarnessContextCapabilityHandler(db, protection);
        var request = new RequestCapability { RequestId = "read-context", Capability = HarnessContextCapabilityHandler.Capability, Payload = JsonPayload.From(new { runId = run.Id, callId = "large-result", offset = 0, limit = 100 }) };
        using var attribution = new InferenceExecutionAttribution(work.Id, Guid.NewGuid(), 1).Enter();
        var results = new List<CapabilityResult>();
        await foreach (var value in handler.HandleAsync(session, request, default)) results.Add(value);
        var result = Assert.Single(results); Assert.True(result.Succeeded, result.Error);
        using var document = JsonDocument.Parse(result.Payload.Span.ToArray());
        Assert.Equal(100, document.RootElement.GetProperty("text").GetString()!.Length);
        Assert.True(document.RootElement.GetProperty("historicalSnapshot").GetBoolean());
        using (new InferenceExecutionAttribution(Guid.NewGuid(), Guid.NewGuid(), 1).Enter())
        {
            await foreach (var value in handler.HandleAsync(session, request, default)) Assert.False(value.Succeeded);
        }
        work.Status = AgentWorkStatus.Cancelled; await db.SaveChangesAsync();
        await foreach (var value in handler.HandleAsync(session, request, default)) Assert.False(value.Succeeded);
    }
    [Theory]
    [InlineData("enabled")]
    [InlineData("unavailable")]
    [InlineData("policy-off")]
    [InlineData("memory-derived")]
    public async Task DecisionCompactionRunsOnlyUnderPressureWithAnEngineAndOrdinaryEvidence(string scenario)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var provider = await AddProviderAsync(db);
        var installation = Guid.NewGuid();
        var session = new AgentSession(Guid.NewGuid().ToString("N"), "test-agent", installation.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>([PlatformCapabilities.LlmChatStream], StringComparer.Ordinal), 1));
        await SeedDispatchAuthorityAsync(db, session);
        db.AgentHarnessPolicies.Add(new() { Id = installation, Revision = 1, PolicyJson = JsonSerializer.Serialize(
            new HarnessPolicy(1, CompactContext: true, DecisionCompaction: scenario != "policy-off"), HarnessWorkspaceService.Json) });
        IDisposable? attribution = null;
        if (scenario == "memory-derived")
        {
            // A chat-turn work item makes the request content-free, which closes the same gate as spill.
            var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = session.BusinessId, AgentInstallationId = installation, Status = AgentWorkStatus.Leased,
                DeadlineAt = DateTimeOffset.UtcNow.AddHours(1), SourceType = "chat-turn" };
            db.Add(work);
            attribution = new InferenceExecutionAttribution(work.Id, Guid.NewGuid(), 1).Enter();
        }
        await db.SaveChangesAsync();
        var engine = new HarnessDecisionCompactionTests.FakeEngine(HarnessDecisionCompactionTests.AnswerByTool(new() { ["read"] = (0.1, 0.1) }));
        var options = new HarnessDecisionModelOptions().Normalize();
        var compactor = new HarnessDecisionCompactor(new HarnessDecisionClient(new HttpClient(engine), options),
            scenario == "unavailable" ? HarnessDecisionEngine.Unavailable("not configured") : HarnessDecisionCompactionTests.Engine(), options, new HarnessDecisionCache());
        var client = new StreamingChatClient();
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(client), new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance,
            decisionCompactor: compactor);
        // Four old results of symbol text push the estimate past window - reserve (256,000 - 38,400).
        var messages = new List<object> { new { role = "user", text = "Find the regression" } };
        for (var i = 0; i < 4; i++)
        {
            messages.Add(new { role = "assistant", contents = new object[] { new { kind = "function_call", callId = $"call-{i}", name = "read", arguments = new { path = $"/src/{i}.cs" } } } });
            messages.Add(new { role = "tool", contents = new object[] { new { kind = "function_result", callId = $"call-{i}", result = new string('!', 62_000) } } });
        }
        messages.AddRange(Enumerable.Range(0, 6).Select(i => (object)new { role = i % 2 == 0 ? "assistant" : "user", text = $"recent {i}" }));
        var request = new RequestCapability { RequestId = "decision", Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(new { providerProfileId = provider, model = "test-model", messages }) };
        var results = new List<CapabilityResult>();
        using (attribution)
            await foreach (var value in handler.StreamAsync(session, request, default)) results.Add(value);

        if (scenario != "enabled")
        {
            Assert.Equal(0, engine.Calls);
            Assert.Contains(results, x => !x.Succeeded && x.Error!.Contains("cannot fit", StringComparison.Ordinal));
            return;
        }
        Assert.All(results, value => Assert.True(value.Succeeded, value.Error));
        Assert.Equal(1, engine.Calls);
        Assert.DoesNotContain(client.ReceivedMessages.SelectMany(x => x.Contents), x => x is Microsoft.Extensions.AI.FunctionCallContent or Microsoft.Extensions.AI.FunctionResultContent);
        Assert.Contains(client.ReceivedMessages, x => x.Text == "recent 5");
        var log = await db.AgentRunLogs.SingleAsync();
        using var settings = JsonDocument.Parse(log.InferenceSettingsJson!);
        var stats = settings.RootElement.GetProperty("decisionCompaction");
        Assert.Equal(4, stats.GetProperty("dropped").GetInt32());
        Assert.Equal("api.typesafe.ai", stats.GetProperty("engine").GetString());
        Assert.DoesNotContain("test-key", log.InferenceSettingsJson);
        Assert.All(settings.RootElement.GetProperty("contextChanges").EnumerateArray(), x => Assert.Equal("decision", x.GetProperty("Method").GetString()));
        Assert.Contains("call-3", log.RequestEvidenceJson); // the original request stays in protected evidence
    }
}
