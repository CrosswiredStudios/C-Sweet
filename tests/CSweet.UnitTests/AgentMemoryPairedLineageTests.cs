using System.Text.Json.Nodes;
using CSweet.AI.Providers;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [Fact]
    public async Task PairedCapturePersistsEveryInputAndLinksEveryDerivedKind()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var assistantId = await AddSourceAssistantAsync(fixture);
        await using var db = fixture.Context();
        // Explicit capture bypasses backfill; pairing must create its own assistant capture marker.
        await fixture.Service(db, new PairedEvidenceProviderFactory()).CaptureMessageAsync(fixture.MessageId, enrich: true);
        var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        Assert.Equal(MemoryCaptureStatus.Completed, job.Status);
        Assert.Equal(3, JsonNode.Parse(job.AcceptedExtractionJson!)!["SchemaVersion"]!.GetValue<int>());
        Assert.NotNull((await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == assistantId)).EpisodeCapturedAt);
        var snapshot = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Equal(2, snapshot.Episodes.Count);
        var evidence = new[] { fixture.MessageId, assistantId }.Order().ToArray();
        Assert.Equal(2, snapshot.Entities.Count);
        Assert.All(snapshot.Entities, x => Assert.Equal(evidence, x.SourceEpisodeIds.Order()));
        Assert.Equal(evidence, Assert.Single(snapshot.Claims).SourceEpisodeIds.Order());
        Assert.Equal(evidence, Assert.Single(snapshot.Edges).SourceEpisodeIds.Order());
        Assert.Equal(evidence, Assert.Single(snapshot.Procedures).SourceEpisodeIds.Order());
        Assert.Equal(MemoryConfirmationState.Pending, snapshot.Procedures.Single().Confirmation);
        var recalled = (await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "Alice")))
            .Where(x => x.Layer == MemoryLayer.Semantic).ToArray();
        Assert.Equal(2, recalled.Length);
        Assert.All(recalled, x => Assert.Equal(evidence, x.EpisodeIds.Order()));
    }

    [Fact]
    public async Task MissingPairedCapturedEpisodeIsNotRecreatedBeforeInference()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var assistantId = await AddSourceAssistantAsync(fixture);
        await using var db = fixture.Context();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        var service = fixture.Service(db, factory);
        await service.CaptureMessageAsync(assistantId, enrich: true);
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        await service.CaptureMessageAsync(fixture.MessageId, enrich: true);
        var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
        Assert.Equal("memory_enrichment_source_invalidated", job.LastError);
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, assistantId));
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Equal(0, factory.Calls);
    }

    [MemoryPostgresFact]
    public async Task AcceptedReplayValidatesPairedEpisodeAndPreservesSafeSingleMessageCompatibility()
    {
        foreach (var scenario in new[] { "missing-assistant", "legacy-pair", "legacy-single" })
        {
            await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
            var assistantId = scenario == "legacy-single" ? (Guid?)null : await AddSourceAssistantAsync(fixture);
            await using var db = fixture.Context();
            await fixture.Store.InitializeAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_paired_claim() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected apply failure'; END $$;
                CREATE TRIGGER reject_paired_claim BEFORE INSERT ON csweet_memory_claims
                    FOR EACH ROW EXECUTE FUNCTION reject_paired_claim();
                """);
            var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
            await fixture.Service(db, factory).CaptureMessageAsync(fixture.MessageId, enrich: true);
            var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
            Assert.NotNull(job.AcceptedExtractionJson);
            if (scenario == "missing-assistant")
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={assistantId!.Value}");
            else
            {
                var envelope = JsonNode.Parse(job.AcceptedExtractionJson!)!;
                envelope["SchemaVersion"] = 2;
                job.AcceptedExtractionJson = envelope.ToJsonString();
            }
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_paired_claim ON csweet_memory_claims; DROP FUNCTION reject_paired_claim();");
            await using var restarted = fixture.Context();
            await fixture.Service(restarted, factory).CaptureMessageAsync(fixture.MessageId, enrich: true);
            job = await restarted.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
            Assert.Equal(1, factory.Calls);
            if (scenario == "legacy-single")
            {
                Assert.Equal(MemoryCaptureStatus.Completed, job.Status);
                Assert.Contains(fixture.MessageId, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims).SourceEpisodeIds);
            }
            else
            {
                Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
                Assert.Equal(scenario == "legacy-pair" ? "memory_enrichment_unverifiable_output" : "memory_enrichment_source_invalidated", job.LastError);
                Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
                if (scenario == "missing-assistant")
                    Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, assistantId!.Value));
            }
        }
    }

    private sealed class PairedEvidenceProviderFactory : ILlmProviderFactory
    {
        public Task<IChatClient> CreateChatClientAsync(Guid id, CancellationToken ct = default) => CreateChatClientAsync(id, null, ct);
        public Task<IChatClient> CreateChatClientAsync(Guid id, string? model, CancellationToken ct = default) =>
            Task.FromResult<IChatClient>(new PairedEvidenceChatClient());
    }

    private sealed class PairedEvidenceChatClient() : DelegatingChatClient(new UsageChatClient())
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Contains(messages, x => x.Text.Contains("Hello Alice.", StringComparison.Ordinal));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """
                {"entities":[{"type":"Person","name":"Alice","aliases":[]},{"type":"Topic","name":"Planning","aliases":[]}],
                 "claims":[{"subjectName":"Alice","predicate":"name","value":"Alice","confidence":1,"importance":1,"sensitivity":"Public"}],
                 "edges":[{"fromName":"Alice","relationship":"plans","toName":"Planning","confidence":1}],
                 "procedures":[{"name":"Planning","procedure":"Ask Alice to review the plan."}]}
                """)));
        }
    }
}
