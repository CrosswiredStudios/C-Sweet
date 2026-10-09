using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Memory;
using CSweet.Memory.Evaluate;

namespace CSweet.Memory.Evaluate.Tests;

public sealed class EvaluationAcceptanceTests
{
    [Fact]
    public void StandalonePolicyCannotPretendToAuthorizeScopedAudiences()
    {
        Assert.Throws<NotSupportedException>(() => CSweet.Infrastructure.Core.MemoryRecallPolicy.MaximumSensitivity(
            new("tenant", "csweet")));
        Assert.Equal(MemorySensitivity.Personal, CSweet.Infrastructure.Core.MemoryRecallPolicy.MaximumSensitivity(
            new("tenant", "memory-evaluation", "employee")));
    }
    [Fact]
    public void CompleteMeasurementsPassCandidateRetrievalGates()
    {
        var questions = Questions();
        Assert.Empty(EvaluationGates.Check(questions, Samples(questions, 2), 2));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-split")]
    [InlineData("fake-recall")]
    [InlineData("nan-latency")]
    [InlineData("missing-instrumentation")]
    [InlineData("missing-abstention")]
    public void IncompleteOrInconsistentMeasurementsCannotPass(string problem)
    {
        var questions = Questions();
        var samples = Samples(questions, 2).ToList();
        switch (problem)
        {
            case "missing": samples.RemoveAt(0); break;
            case "duplicate": samples[1] = samples[0]; break;
            case "wrong-split": samples[0] = samples[0] with { Split = "evaluation" }; break;
            case "fake-recall": samples[0] = samples[0] with { ReturnedIds = [], Returned = 0 }; break;
            case "nan-latency": samples[0] = samples[0] with { Milliseconds = double.NaN }; break;
            case "missing-instrumentation": samples[0] = samples[0] with { DatabaseCommands = 0 }; break;
            case "missing-abstention": samples.RemoveAll(x => x.Required == 0); break;
        }
        Assert.NotEmpty(EvaluationGates.Check(questions, samples, 2));
    }

    [Theory]
    [InlineData("recall")]
    [InlineData("relevance")]
    [InlineData("abstention")]
    [InlineData("forbidden")]
    [InlineData("rendered-size")]
    public void AccurateButUnacceptableMeasurementsFailSpecificGate(string gate)
    {
        var questions = Questions();
        var samples = Samples(questions, 1).ToList();
        var row = gate == "abstention" ? 1 : 0;
        samples[row] = gate switch
        {
            "recall" => samples[row] with { RetrievedRequired = 0, Returned = 0, ReturnedIds = [] },
            "relevance" => samples[row] with { Returned = 2, Irrelevant = 1,
                ReturnedIds = [questions[0].RequiredIds[0].ToString("N"), Guid.NewGuid().ToString("N")] },
            "abstention" => samples[row] with { Returned = 1, Irrelevant = 1, CorrectAbstention = false,
                ReturnedIds = [Guid.NewGuid().ToString("N")] },
            "forbidden" => samples[row] with { Forbidden = 1, RawForbidden = 1 },
            "rendered-size" => samples[row] with { RenderedCharacters = 2049 },
            _ => throw new ArgumentException(gate)
        };
        Assert.Contains(EvaluationGates.Check(questions, samples, 1), x => x.Gate == gate);
    }

    [Fact]
    public void FirstAccessSafetyFailureCannotBeHiddenByWarmResults()
    {
        var questions = Questions();
        var samples = Samples(questions, 1).ToList();
        samples.Add(samples[0] with { Phase = "first-access", Repetition = -1, Forbidden = 1, RawForbidden = 1 });
        Assert.Contains(EvaluationGates.Check(questions, samples, 1), x => x.Gate == "forbidden");
    }

    [Fact]
    public void RelevantSupportingEvidenceDoesNotCountAsIrrelevant()
    {
        var questions = Questions();
        var supporting = Guid.NewGuid();
        questions[0] = questions[0] with { RelevantIds = [questions[0].RequiredIds[0], supporting] };
        var samples = Samples(questions, 1).ToList();
        samples[0] = samples[0] with { Returned = 2,
            ReturnedIds = [questions[0].RequiredIds[0].ToString("N"), supporting.ToString("N")] };
        Assert.Empty(EvaluationGates.Check(questions, samples, 1));
    }

    [Fact]
    public void MissingSplitCannotPass()
    {
        var questions = Questions().Where(x => x.Split == "calibration").ToArray();
        Assert.Contains(EvaluationGates.Check(questions, Samples(questions, 1), 1), x => x.Gate == "coverage");
    }

    [Fact]
    public void EmbeddedCorpusHasStableLabelsAndHashButSeparateOwnedPartitions()
    {
        var first = IndependentCorpus.Load();
        var second = IndependentCorpus.Load();
        Assert.Equal(first.DatasetHash, second.DatasetHash);
        Assert.Equal(JsonSerializer.Serialize(first.Scenarios), JsonSerializer.Serialize(second.Scenarios));
        Assert.NotEqual(first.Partition, second.Partition);
        Assert.Equal(27, first.Scenarios.Count);
        Assert.All(first.Scenarios, x => Assert.False(string.IsNullOrWhiteSpace(x.ExpectedAnswer)));
        Assert.All(new[] { MemoryLayer.Core, MemoryLayer.Episodic, MemoryLayer.Semantic, MemoryLayer.Procedural },
            layer => Assert.Contains(first.Scenarios, q => q.Layer == layer));
    }

    [Theory]
    [InlineData("unknown-property")]
    [InlineData("duplicate-key")]
    [InlineData("wrong-layer")]
    [InlineData("contradictory-labels")]
    [InlineData("missing-source")]
    [InlineData("foreign-source")]
    [InlineData("unknown-label")]
    [InlineData("numeric-enum")]
    [InlineData("null-questions")]
    [InlineData("missing-split")]
    [InlineData("foreign-relevance")]
    public void MalformedDatasetFailsBeforeSeeding(string problem)
    {
        var data = JsonNode.Parse(ReadDataset())!;
        var evidence = data["Evidence"]!.AsArray();
        var questions = data["Questions"]!.AsArray();
        switch (problem)
        {
            case "unknown-property": data["Undocumented"] = true; break;
            case "duplicate-key": evidence[1]!["Key"] = evidence[0]!["Key"]!.GetValue<string>(); break;
            case "wrong-layer": questions[0]!["Required"] = new JsonArray("beech-decision"); break;
            case "contradictory-labels": questions[0]!["Forbidden"] = new JsonArray("beech-claim"); break;
            case "missing-source": evidence[1]!["Source"] = "missing"; break;
            case "foreign-source": evidence[1]!["Owner"] = "foreign"; break;
            case "unknown-label": questions[0]!["Required"] = new JsonArray("missing"); break;
            case "numeric-enum": evidence[0]!["Layer"] = 1; break;
            case "null-questions": data["Questions"] = null; break;
            case "missing-split": foreach (var q in questions) q!["Split"] = "calibration"; break;
            case "foreign-relevance": evidence[0]!["Owner"] = "foreign"; evidence[1]!["Owner"] = "foreign"; break;
        }
        var exception = Record.Exception(() => new IndependentCorpus(data.ToJsonString()));
        Assert.True(exception is InvalidDataException or JsonException, exception?.ToString() ?? "Dataset accepted invalid input.");
    }

    [Fact]
    public void DuplicateJsonPropertyIsRejected()
    {
        var data = ReadDataset().Replace("\"Version\":", "\"Version\":\"duplicate\",\"Version\":", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => new IndependentCorpus(data));
    }

    [Fact]
    public async Task ActualSqliteSearchAndProductionPolicyWithholdForbiddenEvidence()
    {
        var file = Path.Combine(Path.GetTempPath(), $"csweet-evaluation-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteMemoryStore(file);
            await store.InitializeAsync();
            var corpus = IndependentCorpus.Load();
            await corpus.SeedAsync(store, 20, CancellationToken.None);
            foreach (var q in corpus.Scenarios)
            {
                var results = await store.SearchAsync(new(corpus.Partition, MemoryScope.Agent, q.Query,
                    Limit: 10, AsOf: q.AsOf, Layers: new HashSet<MemoryLayer> { q.Layer }));
                var forbidden = corpus.Forbidden.Union(q.ForbiddenIds).ToHashSet();
                var released = results.Where(x => CSweet.Infrastructure.Core.MemoryRecallPolicy.IsEligible(x,
                    CSweet.Infrastructure.Core.MemoryRecallPolicy.MaximumSensitivity(corpus.Partition), q.AsOf));
                Assert.DoesNotContain(released, x => forbidden.Contains(x.Id));
            }
            foreach (var id in new[] { "eval-historical", "eval-current", "eval-locale", "eval-rollback" })
            {
                var q = corpus.Scenarios.Single(x => x.Id == id);
                var results = await store.SearchAsync(new(corpus.Partition, MemoryScope.Agent, q.Query,
                    Limit: 10, AsOf: q.AsOf, Layers: new HashSet<MemoryLayer> { q.Layer }));
                Assert.All(q.RequiredIds, required => Assert.Contains(results, x => x.Id == required));
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(file + suffix); }
    }

    private static string ReadDataset()
    {
        using var stream = typeof(IndependentCorpus).Assembly.GetManifestResourceStream(
            "CSweet.Memory.Evaluate.datasets.employee-retrieval-v3.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static EvaluationScenario[] Questions() => new[] { "calibration", "evaluation" }.SelectMany(split => new[] {
        new EvaluationScenario(split + "-positive", split, "Decision", MemoryLayer.Episodic, [Guid.NewGuid()], DateTimeOffset.UtcNow),
        new EvaluationScenario(split + "-abstain", split, "Unknown", MemoryLayer.Episodic, [], DateTimeOffset.UtcNow)
    }).ToArray();
    private static EvaluationSample[] Samples(IReadOnlyList<EvaluationScenario> questions, int repetitions) => questions.SelectMany(q =>
        Enumerable.Range(0, repetitions).Select(repetition => new EvaluationSample(q.Id, q.Split, "current", q.RequiredIds.Length,
            q.RequiredIds.Length, q.RequiredIds.Length, 0, 0, q.RequiredIds.Length == 0, 1, 1, 100, 25, [],
            q.RequiredIds.Select(x => x.ToString("N")).ToArray()) { Repetition = repetition })).ToArray();
}
