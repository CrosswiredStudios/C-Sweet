using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using CSweet.Memory.Evaluate;

if (args.Length is < 2 or > 5 || args[0] is not ("sqlite" or "postgres") ||
    args.Length == 5 && args[4] is not ("regression" or "independent"))
{
    Console.Error.WriteLine("Usage: CSweet.Memory.Evaluate sqlite|postgres report-directory [distractors=1000] [repetitions=10] [regression|independent]");
    Console.Error.WriteLine("PostgreSQL requires CSWEET_MEMORY_EVAL_POSTGRES and CSWEET_MEMORY_EVAL_DISPOSABLE=1; it creates and removes random evaluation partitions.");
    return 2;
}
var provider = args[0];
if (provider == "postgres" && Environment.GetEnvironmentVariable("CSWEET_MEMORY_EVAL_DISPOSABLE") != "1")
{
    Console.Error.WriteLine("Set CSWEET_MEMORY_EVAL_DISPOSABLE=1 only for an explicitly disposable evaluation database.");
    return 2;
}
if ((args.Length >= 3 && !int.TryParse(args[2], out _)) || (args.Length >= 4 && !int.TryParse(args[3], out _))) return 2;
var distractors = args.Length >= 3 ? int.Parse(args[2]) : 1000;
var repetitions = args.Length >= 4 ? int.Parse(args[3]) : 10;
if (distractors is < 0 or > 50000 || repetitions is < 1 or > 100) return 2;
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
var sqlitePath = Path.Combine(Path.GetTempPath(), $"csweet-memory-evaluation-{Guid.NewGuid():N}.db");
var location = provider == "postgres" ? Environment.GetEnvironmentVariable("CSWEET_MEMORY_EVAL_POSTGRES") : sqlitePath;
if (string.IsNullOrWhiteSpace(location)) { Console.Error.WriteLine("Missing disposable PostgreSQL connection configuration."); return 2; }
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
using var counter = new DatabaseCounter(provider);
await using IMemoryStore store = provider == "postgres" ? new PostgreSqlMemoryStore(location) : new SqliteMemoryStore(location);
var independent = args.Length == 5 && args[4] == "independent";
IEvaluationCorpus corpus = independent ? IndependentCorpus.Load() : new Corpus();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var samples = new List<EvaluationSample>();
var baseline = new EpisodicAndBaseline(provider, location);
var stopwatch = Stopwatch.StartNew();
try
{
    await store.InitializeAsync(timeout.Token);
    await corpus.SeedAsync(store, distractors, timeout.Token);
    var seedingMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    Console.WriteLine($"{provider}: seeded {corpus.Counts["episodes"]} episodes; measuring {corpus.Scenarios.Count} scenarios, {repetitions} repetitions.");
    // Retain the first path access separately; seeding and earlier scenarios may already
    // warm database pages. This is not an OS/database cold-cache experiment.
    for (var repetition = -1; repetition < repetitions; repetition++)
    {
        // Alternate ordering to reduce consistent first/second cache effects.
        foreach (var scenario in repetition < 0 || repetition % 2 == 0 ? corpus.Scenarios : corpus.Scenarios.AsEnumerable().Reverse())
        {
            var modes = repetition >= 0 && scenario.Layer == MemoryLayer.Episodic && scenario.Embedding is null
                ? (repetition % 2 == 0 ? new[] { "current", "episodic-and-ablation" } : new[] { "episodic-and-ablation", "current" })
                : new[] { "current" };
            foreach (var mode in modes)
            {
                var before = counter.Count;
                var started = Stopwatch.GetTimestamp();
                var found = await Search(scenario, mode);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var commands = counter.Count - before;
                var eligible = found.Where(x => MemoryRecallPolicy.IsEligible(x, MemoryRecallPolicy.MaximumSensitivity(corpus.Partition), scenario.AsOf)).ToArray();
                var packed = Pack(eligible);
                var returned = packed.Items.Select(x => x.Id).Distinct().ToArray();
                var relevant = returned.Intersect(scenario.RequiredIds).Count();
                var forbidden = corpus.Forbidden.Union(scenario.ForbiddenIds).ToHashSet();
                samples.Add(new(scenario.Id, scenario.Split, mode, scenario.RequiredIds.Length, relevant, returned.Length,
                    returned.Except(scenario.RelevantIds ?? scenario.RequiredIds).Count(), returned.Count(forbidden.Contains),
                    scenario.RequiredIds.Length == 0 && returned.Length == 0, elapsed, commands,
                    packed.Rendered.Length, (packed.Rendered.Length + 3) / 4,
                    packed.Items.Select(x => x.RetrievalChannel).Distinct().Order().ToArray(), returned.Select(x => x.ToString("N")).ToArray())
                    { Phase = repetition < 0 ? "first-access" : "warm", Repetition = repetition,
                        RawForbidden = found.Select(x => x.Id).Distinct().Count(forbidden.Contains) });
            }
        }
        Console.WriteLine(repetition < 0 ? $"{provider}: first-access measurements complete." : $"{provider}: repetition {repetition + 1}/{repetitions} complete.");
    }
    var summaries = samples.Select(x => (x.Split, x.Mode, x.Phase)).Distinct()
        .Select(key => new { phase = key.Phase, metrics = EvaluationMetrics.Summarize(key.Split, key.Mode,
            samples.Where(x => x.Phase == key.Phase).ToArray()) }).ToArray();
    var sourceRoot = Path.Combine(AppContext.BaseDirectory, "sources");
    var sourceFiles = Directory.GetFiles(sourceRoot, "*.cs").Order(StringComparer.Ordinal).ToArray();
    var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', sourceFiles.Select(file => Path.GetFileName(file) + "\n" + File.ReadAllText(file))))));
    var scenarioHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(corpus.Scenarios))));
    var failures = samples.Where(x => x.Mode == "current" && x.Forbidden > 0).Select(x => x.Scenario).Distinct().ToArray();
    var qualityFailures = independent ? EvaluationGates.Check(corpus.Scenarios, samples, repetitions) : [];
    var uninstrumented = samples.Where(x => MemoryLexicalQuery.Parse(corpus.Scenarios.Single(s => s.Id == x.Scenario).Query).Terms.Count > 0 && x.DatabaseCommands == 0).ToArray();
    var report = new
    {
        dataset = corpus.DatasetVersion, datasetHash = corpus.DatasetHash, scenarioHash, harnessSourceHash = sourceHash,
        harnessSources = sourceFiles.Select(Path.GetFileName).ToArray(),
        harnessAssemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Corpus).Assembly.Location))),
        createdAt = DateTimeOffset.UtcNow,
        provider, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
        storeAssembly = new { version = store.GetType().Assembly.GetName().Version?.ToString(),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(store.GetType().Assembly.Location))) },
        distractors, repetitions, k = 10, corpusCounts = corpus.Counts, scenarios = corpus.Scenarios.Count,
        seedingMilliseconds, elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        databaseCounter = counter.Unit, latency = "First-access and repeated warm store search including provenance reads; excludes packing and model calls. First-access is not a true cold-cache benchmark.",
        context = "Evaluation packs production-eligible candidates with production escaping into a maximum 2048-character rendered allowance; tokens are character/4 estimates, not tokenizer or provider measurements.",
        comparison = "Episodic AND ablation on the same existing indexes and literal terms; not an old-release performance benchmark. Other current channels have no ablation.",
        modelAnswerEvaluation = "Not executed; synthetic embeddings exercise eligibility only and do not establish semantic quality.",
        audienceEvaluation = "Synthetic employee partitions only. Conversation/case authorization requires actual platform acceptance; the standalone adapter throws if asked to resolve them.",
        thresholds = new { profile = independent ? EvaluationGates.Version : "regression-safety-only",
            status = "Candidate engineering thresholds, not approved release SLOs. Answer correctness, concurrency, cold-cache latency and inference cost are unexecuted.",
            recall = independent ? EvaluationGates.MinimumRecall : (double?)null,
            maximumIrrelevantRate = independent ? EvaluationGates.MaximumIrrelevantRate : (double?)null,
            abstention = independent ? EvaluationGates.MinimumAbstention : (double?)null,
            maximumRenderedCharacters = EvaluationGates.MaximumRenderedCharacters, forbiddenResults = 0 },
        retrievalQualityPass = independent ? qualityFailures.Length == 0 : (bool?)null,
        releaseAcceptance = "Incomplete; this report evaluates retrieval only.",
        failures, qualityFailures, uninstrumentedSamples = uninstrumented.Length, summaries, scenariosDefinition = corpus.Scenarios, samples
    };
    var filename = Path.Combine(directory, $"retrieval-{(independent ? "independent" : "regression")}-{provider}-{distractors}.json");
    await File.WriteAllTextAsync(filename, JsonSerializer.Serialize(report, jsonOptions), timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { filename, failures, qualityFailures, uninstrumentedSamples = uninstrumented.Length, summaries }, jsonOptions));
    return failures.Length == 0 && uninstrumented.Length == 0 && qualityFailures.Length == 0 ? 0 : 1;
}
finally
{
    using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    foreach (var partition in new[] { corpus.Partition, corpus.Foreign, corpus.OtherEmployee })
        await store.DeleteScopeAsync(partition, cleanup.Token);
    await store.DisposeAsync();
    if (provider == "sqlite")
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
}

Task<IReadOnlyList<MemoryCandidate>> Search(EvaluationScenario scenario, string mode) => mode == "current"
    ? store.SearchAsync(new(corpus.Partition, MemoryScope.Agent, scenario.Query, Limit: 10, AsOf: scenario.AsOf,
        Layers: new HashSet<MemoryLayer> { scenario.Layer }, Embedding: scenario.Embedding), timeout.Token)
    : baseline.SearchAsync(corpus.Partition, scenario, 10, timeout.Token);

static (MemoryCandidate[] Items, string Rendered) Pack(IEnumerable<MemoryCandidate> candidates)
{
    var rows = new List<MemoryCandidate>();
    var text = new StringBuilder("<memory_context trust=\"untrusted\">\n");
    const string closing = "</memory_context>";
    foreach (var candidate in ReciprocalRankFusion.Rank(candidates).Take(10))
    {
        var row = $"- [memory:{candidate.Id:N}] {MemoryRecallPolicy.EscapeContent(candidate.Content)}\n";
        if (text.Length + row.Length + closing.Length > 2048) continue;
        text.Append(row); rows.Add(candidate);
    }
    text.Append(closing);
    return (rows.ToArray(), text.ToString());
}
