using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    private readonly ITestOutputHelper streamOutput;
    public PlatformLlmCapabilityHandlerTests(ITestOutputHelper output) => streamOutput = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_reasoning_stream_keeps_tracking_bounded_and_persists_every_chunk(bool failOnce)
    {
        const int fragments = 2048;
        var protection = failOnce ? new EphemeralDataProtectionProvider() : null;
        var probe = new StreamPersistenceProbe(streamOutput, failOnce, protection: protection);
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(probe).Options;
        await using var db = new CSweetDbContext(options, protection);
        var provider = await AddProviderAsync(db);
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(new FragmentedReasoningClient(fragments)),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var timer = Stopwatch.StartNew();
        var forwarded = 0;
        var results = await ReadAsync(db, handler, provider, observeResult: async result =>
        {
            if (!result.HasMore || !result.Succeeded) return;
            await using var committed = new CSweetDbContext(options, protection);
            // A separate context must see the receipt before the consumer receives
            // this chunk, including immediately after a transient save failure.
            Assert.Equal(++forwarded, (await committed.AuditOutbox.AsNoTracking().ToListAsync())
                .Count(x => ReadAuditRequest(x, protection).EventType == "model.response.chunk"));
        });
        streamOutput.WriteLine($"Total: {timer.ElapsedMilliseconds} ms; peak tracked audit rows: {probe.PeakAuditRows}");
        Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
        Assert.False(results[^1].HasMore);
        var chunks = results.Where(x => x.HasMore).Select(x => JsonSerializer.Deserialize<ObservedStreamChunk>(x.Payload.Span, JsonOptions)!).ToArray();
        Assert.InRange(chunks.Length, 65, 75); // More than 95% fewer durable writes than per-fragment persistence.
        Assert.Equal(string.Concat(Enumerable.Range(0, fragments).Select(i => $"r{i},")),
            string.Concat(chunks.SelectMany(x => x.Contents ?? []).Where(x => x.Kind == "reasoning").Select(x => x.Text)));
        Assert.Contains(chunks, x => x.FinishReason == "stop");
        Assert.Equal(failOnce, probe.FailureInjected);
        Assert.Single(db.LlmProviderProfiles.Local);
        Assert.Single(db.AgentRuntimeInstances.Local);
        await using var verify = new CSweetDbContext(options, protection);
        var persisted = await verify.AuditOutbox.AsNoTracking().ToListAsync();
        var chunkAudits = persisted.Select(x => ReadAuditRequest(x, protection))
            .Where(x => x.EventType == "model.response.chunk").ToArray();
        Assert.Equal(chunks.Length, chunkAudits.Length);
        Assert.Equal(chunks.Length, chunkAudits.Select(x => x.EventId).Distinct().Count());
        var orderedAudits = chunkAudits.Select(x =>
        {
            using var payload = JsonDocument.Parse(x.Payload!.Value);
            return (Sequence: payload.RootElement.GetProperty("sequence").GetInt32(),
                Chunk: JsonSerializer.Deserialize<ObservedStreamChunk>(x.Payload.Value.Span, JsonOptions)!);
        }).OrderBy(x => x.Sequence).ToArray();
        Assert.Equal(Enumerable.Range(0, chunks.Length), orderedAudits.Select(x => x.Sequence));
        Assert.Equal(string.Concat(Enumerable.Range(0, fragments).Select(i => $"r{i},")),
            string.Concat(orderedAudits.SelectMany(x => x.Chunk.Contents ?? []).Where(x => x.Kind == "reasoning").Select(x => x.Text)));
        if (failOnce) Assert.All(persisted, x => { Assert.Equal("{}", x.RequestJson); Assert.NotNull(x.ProtectedRequest); });
        var run = await verify.AgentRunLogs.SingleAsync();
        Assert.Equal("Completed", run.Status);
        Assert.Equal(fragments, run.ReportedOutputTokens);
        // This is the regression assertion; elapsed timings are evidence, not a flaky gate.
        Assert.True(probe.PeakAuditRows < 32, $"Stream retained {probe.PeakAuditRows} audit rows.");
        Assert.True(probe.PeakActivityWakes < 4, $"Stream retained {probe.PeakActivityWakes} activity hints.");
    }

    [Fact]
    public async Task Persistent_stream_audit_failure_stops_before_forwarding_unaudited_content()
    {
        var probe = new StreamPersistenceProbe(streamOutput, failOnce: false, alwaysFail: true);
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(probe).Options;
        await using var db = new CSweetDbContext(options);
        var provider = await AddProviderAsync(db);
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(new FragmentedReasoningClient(128)),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var results = await ReadAsync(db, handler, provider);
        Assert.True(probe.FailureInjected);
        var failure = Assert.Single(results);
        Assert.False(failure.Succeeded); Assert.True(failure.Retryable);
        Assert.Equal("llm.audit_persistence_failed", failure.FailureCode);
        await using var verify = new CSweetDbContext(options);
        Assert.DoesNotContain(await verify.AuditOutbox.ToListAsync(), x => x.RequestJson.Contains("model.response.chunk"));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(24679)]
    public async Task Shatterpoint_role_repeats_leave_only_ordered_meaningful_receipts(int repeats)
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        // Restart with a new context and handler. The first role in each generation is significant.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = new CSweetDbContext(options);
            var provider = await AddProviderAsync(db);
            var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(new RoleRepeatClient(repeats)),
                new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
                [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
            var results = await ReadAsync(db, handler, provider);
            Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
            var chunks = results.Where(r => r.HasMore).Select(r => JsonSerializer.Deserialize<JsonElement>(r.Payload.Span)).ToArray();
            Assert.Equal(8, chunks.Length);
            Assert.Equal("assistant", chunks[0].GetProperty("role").GetString());
            Assert.Equal("thinking", chunks[1].GetProperty("contents")[0].GetProperty("text").GetString());
            Assert.Equal("file", chunks[2].GetProperty("contents")[0].GetProperty("arguments").GetProperty("path").GetString());
            Assert.Equal("tool", chunks[3].GetProperty("role").GetString());
            Assert.Equal("ok", chunks[4].GetProperty("contents")[0].GetProperty("result").GetString());
            Assert.Equal("done", chunks[5].GetProperty("text").GetString());
            Assert.Equal(17, chunks[6].GetProperty("outputTokenCount").GetInt32());
            Assert.Equal("stop", chunks[7].GetProperty("finishReason").GetString());
            Assert.False(results[^1].HasMore);
            await using var replay = new CSweetDbContext(options);
            var rows = (await replay.AuditOutbox.AsNoTracking().ToListAsync())
                .Select(x => ReadAuditRequest(x, null)).Where(x => x.EventType == "model.response.chunk").GroupBy(x => x.EntityId);
            Assert.Equal(attempt + 1, rows.Count());
            foreach (var run in rows)
            {
                var persisted = run.Select(x => JsonSerializer.Deserialize<JsonElement>(x.Payload!.Value.Span))
                    .OrderBy(x => x.GetProperty("sequence").GetInt32()).ToArray();
                Assert.Equal(Enumerable.Range(0, chunks.Length), persisted.Select(x => x.GetProperty("sequence").GetInt32()));
                for (var i = 0; i < chunks.Length; i++)
                {
                    var delivered = chunks[i].GetProperty("contents");
                    var retained = persisted[i].GetProperty("contents");
                    // The audit projection redacts protectedData, including null placeholders.
                    // Compare the public content and ordering across the transport and storage formats.
                    static IEnumerable<string> ReplayContent(JsonElement array) => array.EnumerateArray().Select(c =>
                        string.Join("|", new[] { "kind", "text", "callId", "name", "arguments", "result" }
                            .Select(field => c.GetProperty(field).GetRawText())));
                    Assert.Equal(ReplayContent(delivered), ReplayContent(retained));
                    Assert.Equal(chunks[i].GetProperty("role").GetRawText(), persisted[i].GetProperty("role").GetRawText());
                    Assert.Equal(chunks[i].GetProperty("finishReason").GetRawText(), persisted[i].GetProperty("finishReason").GetRawText());
                    Assert.Equal(chunks[i].GetProperty("outputTokenCount").GetRawText(), persisted[i].GetProperty("outputTokens").GetRawText());
                }
            }
        }
    }

    [Fact]
    public async Task Role_only_wire_deltas_between_split_tool_arguments_preserve_the_complete_call()
    {
        const string events = """
            data: {"id":"generation","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"read","arguments":"{\"path\":"}}]}}]}

            data: {"id":"generation","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{"role":"assistant"}}]}

            data: {"id":"generation","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"file\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]


            """;
        using var transport = new HttpClient(new WireEvents(events));
        var sdk = new OpenAI.Chat.ChatClient("fixture", new System.ClientModel.ApiKeyCredential("fixture"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri("https://fixture.invalid/v1/"),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(transport) });
        using var client = CSweet.Infrastructure.Llm.OpenAiCompatibleLlmProviderFactory.AdaptChatClient(sdk);
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var provider = await AddProviderAsync(db);
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(client),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var results = await ReadAsync(db, handler, provider);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        var chunks = results.Where(r => r.HasMore).Select(r => JsonSerializer.Deserialize<JsonElement>(r.Payload.Span));
        var call = Assert.Single(chunks.SelectMany(c => c.GetProperty("contents").EnumerateArray()),
            c => c.GetProperty("kind").GetString() == "function_call");
        Assert.Equal("call-1", call.GetProperty("callId").GetString());
        Assert.Equal("read", call.GetProperty("name").GetString());
        Assert.Equal("file", call.GetProperty("arguments").GetProperty("path").GetString());
    }

    private sealed class WireEvents(string events) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(events, System.Text.Encoding.UTF8, "text/event-stream") });
    }

    private sealed class RoleRepeatClient(int repeats) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new(ChatRole.Assistant, []);
            for (var i = 0; i < repeats; i++) yield return new(ChatRole.Assistant, []);
            yield return new(ChatRole.Assistant, [new TextReasoningContent("thinking")]);
            yield return new(ChatRole.Assistant, [new FunctionCallContent("call", "read", new Dictionary<string, object?> { ["path"] = "file" })]);
            yield return new(ChatRole.Tool, []);
            yield return new(ChatRole.Tool, [new FunctionResultContent("call", "ok")]);
            yield return new(ChatRole.Assistant, "done");
            yield return new(null, [new UsageContent(new() { OutputTokenCount = 17 })]);
            yield return new(ChatRole.Assistant, []) { FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record ObservedStreamChunk(string? Text, IReadOnlyList<ObservedStreamContent>? Contents, string? FinishReason);
    private sealed record ObservedStreamContent(string Kind, string? Text);

    private static AuditEventWriteRequest ReadAuditRequest(ComputeAuditOutbox row, IDataProtectionProvider? protection) =>
        JsonSerializer.Deserialize<AuditEventWriteRequest>(row.ProtectedRequest is null ? row.RequestJson :
            System.Text.Encoding.UTF8.GetString(protection!.CreateProtector("CSweet.AuditOutbox.v1").Unprotect(row.ProtectedRequest)))!;

    private sealed class StreamPersistenceProbe(ITestOutputHelper output, bool failOnce = false, bool alwaysFail = false,
        IDataProtectionProvider? protection = null) : SaveChangesInterceptor
    {
        public int PeakAuditRows { get; private set; }
        public int PeakActivityWakes { get; private set; }
        public bool FailureInjected { get; private set; }
        private int saves;
        private readonly Stopwatch block = Stopwatch.StartNew();
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var count = eventData.Context!.ChangeTracker.Entries<ComputeAuditOutbox>().Count();
            PeakAuditRows = Math.Max(PeakAuditRows, count);
            PeakActivityWakes = Math.Max(PeakActivityWakes, eventData.Context.ChangeTracker
                .Entries<CSweet.Domain.Notifications.ApplicationRealtimeOutboxItem>()
                .Count(x => x.Entity.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.CurrentActivityChanged));
            if (++saves % 512 == 0)
            {
                output.WriteLine($"Saves {saves}: block {block.ElapsedMilliseconds} ms; tracked audit rows {count}");
                block.Restart();
            }
            // The run record is created before streaming; inject only when a chunk
            // receipt is queued, including encrypted outbox payloads.
            var pendingChunk = (failOnce || alwaysFail) && eventData.Context.ChangeTracker.Entries<ComputeAuditOutbox>()
                .Any(x => x.State == EntityState.Added && ReadAuditRequest(x.Entity, protection).EventType == "model.response.chunk");
            if (pendingChunk && (alwaysFail || (failOnce && !FailureInjected)))
            {
                FailureInjected = true;
                throw new InvalidOperationException("Injected transient stream audit save failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FragmentedReasoningClient(int count) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(ChatRole.Assistant, [new TextReasoningContent($"r{i},")]);
            }
            yield return new(ChatRole.Assistant, "done");
            yield return new(null, [new UsageContent(new() { InputTokenCount = 5, OutputTokenCount = count })]);
            yield return new(ChatRole.Assistant, []) { FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
