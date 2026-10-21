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
        var probe = new StreamPersistenceProbe(streamOutput, failOnce);
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(probe).Options;
        var protection = failOnce ? new EphemeralDataProtectionProvider() : null;
        await using var db = new CSweetDbContext(options, protection);
        var provider = await AddProviderAsync(db);
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(new FragmentedReasoningClient(fragments)),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var timer = Stopwatch.StartNew();
        var results = await ReadAsync(db, handler, provider);
        streamOutput.WriteLine($"Total: {timer.ElapsedMilliseconds} ms; peak tracked audit rows: {probe.PeakAuditRows}");
        Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
        Assert.False(results[^1].HasMore);
        var chunks = results.Where(x => x.HasMore).Select(x => JsonSerializer.Deserialize<ObservedStreamChunk>(x.Payload.Span, JsonOptions)!).ToArray();
        Assert.Equal(string.Concat(Enumerable.Range(0, fragments).Select(i => $"r{i},")),
            string.Concat(chunks.SelectMany(x => x.Contents ?? []).Where(x => x.Kind == "reasoning").Select(x => x.Text)));
        Assert.Contains(chunks, x => x.FinishReason == "stop");
        Assert.Equal(failOnce, probe.FailureInjected);
        Assert.Single(db.LlmProviderProfiles.Local);
        Assert.Single(db.AgentRuntimeInstances.Local);
        await using var verify = new CSweetDbContext(options, protection);
        var persisted = await verify.AuditOutbox.AsNoTracking().ToListAsync();
        var chunkAudits = persisted.Select(x => JsonSerializer.Deserialize<AuditEventWriteRequest>(x.ProtectedRequest is null
                ? x.RequestJson : System.Text.Encoding.UTF8.GetString(protection!.CreateProtector("CSweet.AuditOutbox.v1").Unprotect(x.ProtectedRequest)))!)
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

    private sealed record ObservedStreamChunk(string? Text, IReadOnlyList<ObservedStreamContent>? Contents, string? FinishReason);
    private sealed record ObservedStreamContent(string Kind, string? Text);

    private sealed class StreamPersistenceProbe(ITestOutputHelper output, bool failOnce = false) : SaveChangesInterceptor
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
            if (failOnce && saves == 1024)
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
