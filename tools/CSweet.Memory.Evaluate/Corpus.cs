using System.Security.Cryptography;
using System.Text;
using CSweet.Memory;

namespace CSweet.Memory.Evaluate;

public sealed record EvaluationScenario(string Id, string Split, string Query, MemoryLayer Layer,
    Guid[] RequiredIds, DateTimeOffset AsOf, IReadOnlyList<float>? Embedding = null);

internal sealed class Corpus
{
    public const string Version = "employee-business-retrieval-v2";
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    public MemoryPartition Partition { get; } = new(Guid.NewGuid().ToString("D"), "memory-evaluation", "employee-avery");
    public MemoryPartition Foreign => Partition with { TenantId = Partition.TenantId + "-foreign" };
    public MemoryPartition OtherEmployee => Partition with { AgentId = "employee-other" };
    public HashSet<Guid> Forbidden { get; } = [];
    public List<EvaluationScenario> Scenarios { get; } = [];
    public Dictionary<string, int> Counts { get; } = [];

    public static Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(Version + ":" + key)).AsSpan(0, 16));

    public async Task SeedAsync(IMemoryStore store, int distractors, CancellationToken token)
    {
        var facts = new[]
        {
            ("billing", "Alice", "selected", "PostgreSQL", "CSM-42 billing repository uses PostgreSQL for transactional invoices.", "Which database did Alice select for CSM-42?", "Tell me the transactional invoices database decision", "Ally"),
            ("release", "Morgan", "requires", "SHA256", "Release RLS-91 requires SHA256 checksum verification before promotion.", "What does Morgan require for RLS-91?", "Which checksum must be verified before promotion?", "Mo"),
            ("accessibility", "Quinn", "sets", "contrast 4.5:1", "Quinn sets the accessibility contrast threshold to 4.5:1 for UI-18.", "What contrast threshold did Quinn set?", "Tell me the UI-18 accessibility threshold", "Q"),
            ("locale", "Sora", "supports", "café 東京", "Sora supports café and 東京 localization in the locale repository LOC-7.", "Which localization does Sora support?", "What is supported in LOC-7 locale repository?", "空"),
            ("engine", "Avery", "prefers", "Godot", "Avery prefers Godot for the GAME-12 prototype and keeps frame pacing under 16ms.", "Which engine does Avery prefer?", "Tell me the GAME-12 prototype engine preference", "Av"),
            ("operations", "Riley", "scheduled", "nightly snapshot", "Riley scheduled a nightly snapshot for OPS-8 disaster recovery.", "What recovery did Riley schedule?", "Tell me the OPS-8 disaster recovery schedule", "Ry")
        };
        foreach (var (key, person, predicate, value, content, calibration, heldout, alias) in facts)
        {
            var episode = Episode(key, content);
            await AddEpisode(store, episode, token);
            var entity = new MemoryEntity(Id(key + ":person"), Partition, "person", person, [alias], "employee:" + person,
                false, Now.AddDays(-10), Now.AddDays(-10)) { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [episode.Id] };
            await store.UpsertEntityAsync(entity, token); Increment("entities");
            var claim = new MemoryClaim(Id(key + ":claim"), Partition, episode.Id, entity.Id, predicate, null, value,
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, 1, 1,
                Now.AddDays(-5), null, Now.AddDays(-4));
            await store.WriteClaimAsync(claim, token); Increment("claims");
            Scenarios.Add(new(key + "-calibration", "calibration", calibration, MemoryLayer.Semantic, [claim.Id], Now));
            Scenarios.Add(new(key + "-heldout", "held-out", heldout, MemoryLayer.Episodic, [episode.Id], Now));
            Scenarios.Add(new(key + "-alias", "held-out", alias, MemoryLayer.Semantic, [claim.Id], Now));
            foreach (var state in new[] { "rejected", "pending", "future", "ended" })
            {
                var invalid = claim with { Id = Id(key + ":" + state), Value = value + " FORBIDDEN-" + state,
                    Confirmation = state == "pending" ? MemoryConfirmationState.Pending : state == "rejected" ? MemoryConfirmationState.Rejected : claim.Confirmation,
                    ValidFrom = state == "future" ? Now.AddDays(1) : claim.ValidFrom, ValidTo = state == "ended" ? Now : null };
                await store.WriteClaimAsync(invalid, token); Increment("claims"); Forbidden.Add(invalid.Id);
            }
        }
        var recoverySource = Episode("procedure-source", "Restore service by validating the snapshot and checksums before reopening writes.");
        await AddEpisode(store, recoverySource, token);
        var procedure = new ProceduralMemory(Id("restore-procedure"), Partition, recoverySource.Id, "Restore billing service",
            "Stop writes; restore the snapshot; verify SHA256 checksums; reopen writes only after verification.",
            "CSM-42 billing incident recovery", 1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed,
            Now.AddDays(-1), Now.AddDays(1), Now.AddDays(-1));
        await store.WriteProcedureAsync(procedure, token); Increment("procedures");
        Scenarios.Add(new("procedure-calibration", "calibration", "Restore billing service", MemoryLayer.Procedural, [procedure.Id], Now));
        Scenarios.Add(new("procedure-heldout", "held-out", "How should I recover CSM-42 after an incident?", MemoryLayer.Procedural, [procedure.Id], Now));
        Scenarios.Add(new("procedure-expired", "held-out", "snapshot checksums", MemoryLayer.Procedural, [], Now.AddDays(1)));
        var block = new MemoryBlock(Id("core-preference"), Partition, "Reply preference", "Use concise weekly updates with explicit risks and next actions.",
            1, 128, true, MemoryTrustTier.ConfirmedUser, Now.AddDays(-1)) { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [recoverySource.Id] };
        await store.WriteBlockAsync(block, token); Increment("blocks");
        Scenarios.Add(new("core-preference", "held-out", "How should weekly updates be written?", MemoryLayer.Core, [block.Id], Now));
        await store.WriteBlockAsync(block with { Id = Id("future-core"), Name = "Future preference", UpdatedAt = Now.AddDays(1) }, token);
        Increment("blocks"); Forbidden.Add(Id("future-core"));

        var historicSource = Episode("history-source", "Nimbus deployment decision was changed from Canary to BlueGreen.");
        await AddEpisode(store, historicSource, token);
        var historicEntity = new MemoryEntity(Id("history-person"), Partition, "service", "Nimbus", [], null, false, Now.AddDays(-5), Now.AddDays(-5))
            { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [historicSource.Id] };
        await store.UpsertEntityAsync(historicEntity, token); Increment("entities");
        var historic = new MemoryClaim(Id("history-old"), Partition, historicSource.Id, historicEntity.Id, "deployment", null, "Canary",
            MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, 1, 1, Now.AddDays(-5), null, Now.AddDays(3));
        var current = historic with { Id = Id("history-current"), Value = "BlueGreen", ValidFrom = Now, SupersedesClaimId = historic.Id };
        await store.WriteClaimAsync(historic, token); await store.WriteClaimAsync(current, token);
        await store.SupersedeClaimAsync(historic.Id, current.Id, Now, token); Increment("claims", 2);
        Scenarios.Add(new("historical-valid-time", "held-out", "Nimbus", MemoryLayer.Semantic, [historic.Id], Now.AddTicks(-1)));
        Scenarios.Add(new("current-supersession", "held-out", "Nimbus", MemoryLayer.Semantic, [current.Id], Now));

        foreach (var state in new[] { "future-source", "expired-source", "restricted-source", "foreign-tenant", "other-employee" })
        {
            var source = Episode(state, "PrivateZephyr employment decision FORBIDDEN " + state) with
            {
                Partition = state == "foreign-tenant" ? Foreign : state == "other-employee" ? OtherEmployee : Partition,
                OccurredAt = state == "future-source" ? Now.AddDays(1) : Now.AddDays(-5),
                ExpiresAt = state == "expired-source" ? Now : null,
                Sensitivity = state == "restricted-source" ? MemorySensitivity.Restricted : MemorySensitivity.Internal
            };
            await AddEpisode(store, source, token); Forbidden.Add(source.Id);
            var entity = new MemoryEntity(Id(state + ":entity"), source.Partition, "person", "PrivateZephyr", [], null, false, Now.AddDays(-5), Now.AddDays(-5))
                { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [source.Id] };
            await store.UpsertEntityAsync(entity, token); Increment("entities");
            var badClaim = new MemoryClaim(Id(state + ":claim"), source.Partition, source.Id, entity.Id, "decision", null, "FORBIDDEN " + state,
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, 1, 1, Now.AddDays(-5), null, Now);
            await store.WriteClaimAsync(badClaim, token); Increment("claims"); Forbidden.Add(badClaim.Id);
            await store.WriteEmbeddingAsync(new(Id(state + ":embedding"), source.Partition, source.Id, MemoryLayer.Episodic, [1, 0], "synthetic-v1", Now), token);
            Increment("embeddings");
        }
        Scenarios.Add(new("source-policy-episodic", "held-out", "PrivateZephyr", MemoryLayer.Episodic, [], Now));
        Scenarios.Add(new("source-policy-semantic", "held-out", "PrivateZephyr", MemoryLayer.Semantic, [], Now));
        Scenarios.Add(new("vector-policy", "held-out", "PrivateZephyr", MemoryLayer.Episodic, [], Now, [1, 0]));
        foreach (var query in new[] { "quasar dolphin", "", "%%% !!!", "the and is" })
            Scenarios.Add(new("abstain-" + Scenarios.Count, "held-out", query, MemoryLayer.Episodic, [], Now));
        Scenarios.Add(new("identifier", "held-out", "CSM-42", MemoryLayer.Episodic, [Id("billing")], Now));
        Scenarios.Add(new("unicode", "held-out", "東京", MemoryLayer.Episodic, [Id("locale")], Now));
        Scenarios.Add(new("long-query", "held-out", "CSM-42 " + string.Join(' ', Enumerable.Repeat("noise", 10000)), MemoryLayer.Episodic, [Id("billing")], Now));
        for (var i = 0; i < distractors; i++)
        {
            var distractor = Episode("distractor-" + i, $"Stockroom inventory widget WID-{i:D6} requires shelf inspection and warehouse recount.");
            await AddEpisode(store, distractor, token);
            // Near-neighbor lexical distractors are measured separately from unrelated stockroom evidence.
            if (i % 10 == 0)
                await AddEpisode(store, Episode("neighbor-" + i, $"CSM-42 billing archived meeting {i}: coffee delivery and office seating."), token);
        }
    }

    private MemoryEpisode Episode(string key, string content) => new(Id(key), Partition, MemoryScope.Agent, content, "text/plain",
        new("user", "evaluation:" + key), key, Now.AddDays(-10), Now.AddDays(-10));
    private async Task AddEpisode(IMemoryStore store, MemoryEpisode episode, CancellationToken token)
    { await store.AppendEpisodeAsync(episode, token); Increment("episodes"); }
    private void Increment(string kind, int amount = 1) => Counts[kind] = Counts.GetValueOrDefault(kind) + amount;
}
