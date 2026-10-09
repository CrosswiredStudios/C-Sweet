using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Memory;

namespace CSweet.Memory.Evaluate;

// This data is frozen before the first retrieval measurement. Any later tuning makes it
// regression evidence; it must not be renamed to suggest a new blind estimate.
internal sealed class IndependentCorpus : IEvaluationCorpus
{
    public string DatasetVersion => definition.Version;
    public string DatasetHash { get; }
    public MemoryPartition Partition { get; } = new(Guid.NewGuid().ToString("D"), "memory-evaluation", "employee-inez");
    public MemoryPartition Foreign => Partition with { TenantId = Partition.TenantId + "-foreign" };
    public MemoryPartition OtherEmployee => Partition with { AgentId = "employee-other" };
    public HashSet<Guid> Forbidden { get; } = [];
    public List<EvaluationScenario> Scenarios { get; } = [];
    public Dictionary<string, int> Counts { get; } = [];
    private readonly DatasetDefinition definition;

    public static IndependentCorpus Load()
    {
        using var stream = typeof(IndependentCorpus).Assembly.GetManifestResourceStream(
            "CSweet.Memory.Evaluate.datasets.employee-retrieval-v3.json")!;
        using var reader = new StreamReader(stream);
        return new(reader.ReadToEnd());
    }

    internal IndependentCorpus(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 262144) throw new InvalidDataException("Dataset exceeds 256 KiB.");
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        using (var document = JsonDocument.Parse(bytes)) RejectDuplicateProperties(document.RootElement);
        definition = JsonSerializer.Deserialize<DatasetDefinition>(bytes, options)
            ?? throw new InvalidDataException("Missing dataset.");
        Validate(definition);
        DatasetHash = Convert.ToHexString(SHA256.HashData(bytes));
        foreach (var row in definition.Evidence)
            if (row.Owner != "self" || row.Sensitivity == MemorySensitivity.Restricted ||
                row.Confirmation != MemoryConfirmationState.Confirmed)
                Forbidden.Add(Id(row.Key));
        foreach (var row in definition.Questions)
            Scenarios.Add(new(row.Id, row.Split, row.Query, row.Layer, row.Required.Select(Id).ToArray(), row.AsOf ?? definition.AsOf)
            {
                RelevantIds = (row.Relevant ?? row.Required).Select(Id).ToArray(),
                ForbiddenIds = row.Forbidden.Select(Id).ToArray(), ExpectedAnswer = row.ExpectedAnswer, Category = row.Category
            });
    }

    private Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(DatasetVersion + ":" + key)).AsSpan(0, 16));

    public async Task SeedAsync(IMemoryStore store, int distractors, CancellationToken token)
    {
        // Independent of JSON row ordering, every derivative's source exists first.
        foreach (var row in definition.Evidence.Where(x => x.Layer == MemoryLayer.Episodic))
            await AddEpisode(row, token);
        foreach (var row in definition.Evidence.Where(x => x.Layer != MemoryLayer.Episodic))
        {
            var source = definition.Evidence.Single(x => x.Key == row.Source);
            var partition = Owner(row.Owner);
            var from = row.ValidFrom ?? definition.AsOf.AddDays(-5);
            var updated = row.UpdatedAt ?? from;
            switch (row.Layer)
            {
                case MemoryLayer.Semantic:
                    var entity = new MemoryEntity(Id(row.Key + ":entity"), partition, "evaluation-subject", row.Subject!,
                        row.Aliases, null, false, from, updated)
                        { Sensitivity = row.Sensitivity, SourceEpisodeIds = [Id(source.Key)] };
                    // Upsert may retain an existing canonical identity. Claims must use
                    // the returned identity, never the proposed new entity id.
                    var written = await store.UpsertEntityAsync(entity, token); Increment("entities");
                    await store.WriteClaimAsync(new(Id(row.Key), partition, Id(source.Key), written.Id, row.Predicate!,
                        null, row.Text, MemoryTrustTier.ConfirmedUser, row.Confirmation, row.Sensitivity, 1, 1,
                        from, row.ValidTo, updated), token); Increment("claims");
                    break;
                case MemoryLayer.Procedural:
                    await store.WriteProcedureAsync(new(Id(row.Key), partition, Id(source.Key), row.Name!, row.Text,
                        row.Trigger!, 1, MemoryTrustTier.ConfirmedUser, row.Confirmation, from, row.ValidTo, updated), token);
                    Increment("procedures");
                    break;
                case MemoryLayer.Core:
                    await store.WriteBlockAsync(new(Id(row.Key), partition, row.Name!, row.Text, 1, 256, true,
                        MemoryTrustTier.ConfirmedUser, updated)
                        { Sensitivity = row.Sensitivity, SourceEpisodeIds = [Id(source.Key)], Confirmation = row.Confirmation }, token);
                    Increment("blocks");
                    break;
            }
        }
        for (var i = 0; i < distractors; i++)
            await AddEpisode(new() { Key = "distractor-" + i, Layer = MemoryLayer.Episodic,
                Text = i % 10 == 0
                    ? $"LANTERN-63 project meeting {i} discussed snack orders and printer toner deliveries."
                    : $"Warehouse stock WT-{i:D6} needs a forklift inventory count and shelf inspection." }, token);

        async Task AddEpisode(EvidenceDefinition row, CancellationToken cancellationToken)
        {
            await store.AppendEpisodeAsync(new(Id(row.Key), Owner(row.Owner), MemoryScope.Agent, row.Text, "text/plain",
                new("user", "evaluation:" + row.Key), row.Key, row.ValidFrom ?? definition.AsOf.AddDays(-10),
                row.UpdatedAt ?? definition.AsOf.AddDays(-10))
                { Sensitivity = row.Sensitivity, ExpiresAt = row.ValidTo }, cancellationToken);
            Increment("episodes");
        }
    }

    private MemoryPartition Owner(string owner) => owner switch
    { "self" => Partition, "foreign" => Foreign, "other-employee" => OtherEmployee, _ => throw new InvalidDataException("Invalid owner.") };
    private void Increment(string kind) => Counts[kind] = Counts.GetValueOrDefault(kind) + 1;

    private static void Validate(DatasetDefinition dataset)
    {
        if (string.IsNullOrWhiteSpace(dataset.Version) || dataset.Version.Length > 128 || dataset.AsOf == default ||
            dataset.Evidence is null || dataset.Questions is null || dataset.Evidence.Length is < 4 or > 256 ||
            dataset.Questions.Length is < 4 or > 128) throw new InvalidDataException("Invalid dataset bounds.");
        var evidence = new Dictionary<string, EvidenceDefinition>(StringComparer.Ordinal);
        foreach (var row in dataset.Evidence)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Key) || row.Key.Length > 100 ||
                row.Key.StartsWith("distractor-", StringComparison.Ordinal) || row.Key.EndsWith(":entity", StringComparison.Ordinal) ||
                !evidence.TryAdd(row.Key, row) || string.IsNullOrWhiteSpace(row.Text) || row.Text.Length > 4096 ||
                row.Owner is not ("self" or "foreign" or "other-employee") || row.Aliases is null || row.Aliases.Length > 8 ||
                row.Aliases.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128) ||
                row.Layer is not (MemoryLayer.Episodic or MemoryLayer.Semantic or MemoryLayer.Procedural or MemoryLayer.Core) ||
                !Enum.IsDefined(row.Sensitivity) || !Enum.IsDefined(row.Confirmation) ||
                row.ValidTo is {} end && end <= (row.ValidFrom ?? dataset.AsOf.AddDays(row.Layer == MemoryLayer.Episodic ? -10 : -5)))
                throw new InvalidDataException("Invalid or duplicate evidence row.");
        }
        foreach (var row in dataset.Evidence.Where(x => x.Layer != MemoryLayer.Episodic))
        {
            if (row.Source is null || !evidence.TryGetValue(row.Source, out var source) ||
                source.Layer != MemoryLayer.Episodic || source.Owner != row.Owner ||
                row.Layer == MemoryLayer.Semantic && (string.IsNullOrWhiteSpace(row.Subject) || string.IsNullOrWhiteSpace(row.Predicate)) ||
                row.Layer is MemoryLayer.Core or MemoryLayer.Procedural && string.IsNullOrWhiteSpace(row.Name) ||
                row.Layer == MemoryLayer.Procedural && string.IsNullOrWhiteSpace(row.Trigger))
                throw new InvalidDataException("Invalid source or missing layer fields.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in dataset.Questions)
        {
            if (q is null || string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 100 || !ids.Add(q.Id) ||
                q.Split is not ("calibration" or "evaluation") || q.Query is null || q.Query.Length > 32768 ||
                string.IsNullOrWhiteSpace(q.Category) || string.IsNullOrWhiteSpace(q.ExpectedAnswer) ||
                q.Required is null || q.Forbidden is null) throw new InvalidDataException("Invalid question.");
            var relevant = q.Relevant ?? q.Required;
            foreach (var labels in new[] { q.Required, relevant, q.Forbidden })
                if (labels.Length > 64 || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length ||
                    labels.Any(x => !evidence.TryGetValue(x, out var row) || row.Layer != q.Layer))
                    throw new InvalidDataException("Unknown, duplicate or wrong-layer labels.");
            if (q.Required.Except(relevant).Any() || relevant.Intersect(q.Forbidden).Any() ||
                q.Required.Length == 0 && relevant.Length > 0 ||
                relevant.Any(x => evidence[x].Owner != "self" || evidence[x].Sensitivity == MemorySensitivity.Restricted ||
                    evidence[x].Confirmation != MemoryConfirmationState.Confirmed))
                throw new InvalidDataException("Contradictory relevance labels.");
        }
        foreach (var split in new[] { "calibration", "evaluation" })
            if (!dataset.Questions.Any(x => x.Split == split && x.Required.Length > 0) ||
                !dataset.Questions.Any(x => x.Split == split && x.Required.Length == 0))
                throw new InvalidDataException("Each split requires positive and abstention questions.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property."); RejectDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

internal sealed record DatasetDefinition
{
    public required string Version { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public required EvidenceDefinition[] Evidence { get; init; }
    public required QuestionDefinition[] Questions { get; init; }
}
internal sealed record EvidenceDefinition
{
    public required string Key { get; init; }
    public required MemoryLayer Layer { get; init; }
    public required string Text { get; init; }
    public string Owner { get; init; } = "self";
    public string? Source { get; init; }
    public string? Subject { get; init; }
    public string[] Aliases { get; init; } = [];
    public string? Predicate { get; init; }
    public string? Name { get; init; }
    public string? Trigger { get; init; }
    public MemorySensitivity Sensitivity { get; init; } = MemorySensitivity.Internal;
    public MemoryConfirmationState Confirmation { get; init; } = MemoryConfirmationState.Confirmed;
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
internal sealed record QuestionDefinition
{
    public required string Id { get; init; }
    public required string Split { get; init; }
    public required string Query { get; init; }
    public required MemoryLayer Layer { get; init; }
    public required string[] Required { get; init; }
    public string[]? Relevant { get; init; }
    public string[] Forbidden { get; init; } = [];
    public DateTimeOffset? AsOf { get; init; }
    public required string ExpectedAnswer { get; init; }
    public required string Category { get; init; }
}
