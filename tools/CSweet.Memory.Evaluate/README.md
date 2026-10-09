# Memory retrieval evaluation

`Corpus.SeedAsync` defines `employee-business-retrieval-v2`: six employee/business decisions,
source-linked claims and aliases, procedure/core context, valid-time supersession, forbidden
states and audiences, vector eligibility, identifiers, Unicode, abstentions, and near-neighbor
distractors. Seven calibration queries and 27 originally held-out queries remain separate in
every report. The 0.2.0 findings informed the 0.2.1 alias and ranking fixes, so this corpus now
provides regression evidence, not a blind quality estimate. Preserve its questions and expected
answers for before/after comparison; use a fresh independent set for future quality acceptance.

`IndependentCorpus` adds the frozen synthetic `datasets/employee-retrieval-v3.json` baseline:
seven calibration and twenty evaluation questions with separate business facts, expected
answer rubrics, required evidence, optional helpful evidence, and scenario-specific forbidden
evidence. The dataset covers all four retrieval layers, identifiers, aliases, Unicode,
contradictions, temporal boundaries, unsupported questions and restricted/foreign sources.
It is embedded in the evaluator assembly, strictly validated before seeding, and hashed in
every report. Canonical entity upserts use the store's returned ID for claim references.
This baseline was created before further retrieval tuning. Once its findings guide tuning,
it becomes regression evidence; final independent acceptance needs a newly frozen set.

Build this standalone tool against the same memory packages that the application is testing.
For the current 0.5.0 feed, from the C-Sweet repository:

```powershell
dotnet restore tools/CSweet.Memory.Evaluate/CSweet.Memory.Evaluate.csproj --configfile tools/CSweet.Memory.Evaluate/NuGet.local.config
dotnet build tools/CSweet.Memory.Evaluate/CSweet.Memory.Evaluate.csproj -c Release --no-restore
dotnet tools/CSweet.Memory.Evaluate/bin/Release/net10.0/CSweet.Memory.Evaluate.dll sqlite artifacts/memory-retrieval-evaluation/0.5.0 1000 5
dotnet tools/CSweet.Memory.Evaluate/bin/Release/net10.0/CSweet.Memory.Evaluate.dll sqlite artifacts/memory-retrieval-evaluation/0.5.0 1000 5 independent
```

`NuGet.local.config` explicitly selects the draft feed and nuget.org. Build/pack Memory first;
the draft packages are not yet published. Keep reports from different package versions in
separate directories. The command-line `--source` URL was interpreted as a local path by the
installed Windows SDK during verification; the explicit configuration avoids that failure.

PostgreSQL requires a disposable instance. Set `CSWEET_MEMORY_EVAL_POSTGRES` to its connection
string and `CSWEET_MEMORY_EVAL_DISPOSABLE=1`, then use `postgres` instead of `sqlite`. The tool
creates random partitions, initializes the normal store schema, and removes only its three
evaluation partitions. SQLite uses a fresh temporary file and removes it after evaluation.
Distractor counts are bounded to 0–50,000 and repetitions to 1–100. The command returns a
failure for forbidden released results or missing database instrumentation. The default
`regression` profile retains safety-only exit semantics. The `independent` profile also fails
on `EvaluationGates` candidate thresholds, checked separately for both splits: weighted
recall at least 90%, irrelevant returned fraction at most 15%, correct abstention 100%,
and rendered context at most 2,048 characters. Missing/duplicate repetitions, missing splits,
inconsistent sample counts/IDs and nonfinite timing cannot produce a passing report.
These are engineering proposals established before observing the new baseline, not approved
production SLOs. Exit zero never establishes complete memory release acceptance.

`EvaluationMetrics` reports weighted recall@10, irrelevant results, correct abstentions,
forbidden results, first-access/warm p50/p95 search latency, database executions and rendered context size.
Candidate eligibility and escaping compile the actual platform `MemoryRecallPolicy`; packing
is explicitly an evaluation policy with a 2,048-character rendered allowance. It does not
claim end-to-end platform invocation budget enforcement. Search timing includes source reads
and excludes packing/model calls. Full JSON retains individual samples and fixture IDs.
Reports fingerprint all compiled source files (including the linked recall policy), the
evaluator assembly, scenarios and store assembly, and record runtime and corpus configuration.
First-access samples precede repeated search, but seeding and earlier queries may already warm
pages; they are not true cold-cache measurements. Summary sample counts are repeated query
observations, not independent business examples or confidence intervals.

`RawForbidden` records flagged candidates before platform eligibility/packing; stores can
return restricted candidates with restrictive metadata for the platform policy to withhold.
The zero-release gate uses `Forbidden` after production eligibility and packing. This
standalone evaluator uses employee partitions only. Its scoped-audience adapter throws if
asked to authorize a conversation or case; it cannot replace actual platform authorization
tests. Helpful evidence can be judged relevant without being required; abstention questions
must have neither required nor relevant labels.

`DatabaseCounter` observes Npgsql client activities and native SQLite statement traces in this
isolated process, without retaining SQL or parameters. PostgreSQL batches count as one command;
SQLite traces include connection PRAGMAs and trigger statements. Their numerical counts are
different units and must not be treated as a cross-store efficiency ratio.

`EpisodicAndBaseline` is a controlled AND-versus-OR ablation using the same literal terms and
existing episodic indexes. It is not an old-release latency benchmark. Compare the **same
episodic scenarios** when judging recall change; the full current summary also includes other
layers. The baseline applies source/policy checks after its candidate limit, so it does not
prove equivalent candidate selection or relative production latency. No model answer quality,
embedding quality, inference costs, production SLO, or concurrent workload guarantee is claimed.

Keep report generation separate from assertion about release readiness. Review failures and
measured quality/latency before agreeing release thresholds; preserve calibration and held-out
results rather than removing a failing scenario to improve the score.

Run harness acceptance separately from retrieval findings:

```powershell
dotnet restore tools/CSweet.Memory.Evaluate.Tests/CSweet.Memory.Evaluate.Tests.csproj --configfile tools/CSweet.Memory.Evaluate/NuGet.local.config -p:UseLocalCSweetMemory=false
dotnet test tools/CSweet.Memory.Evaluate.Tests/CSweet.Memory.Evaluate.Tests.csproj -c Release --no-restore -p:UseLocalCSweetMemory=false
```

`EvaluationAcceptanceTests` checks malformed datasets, canonical identity references, actual
SQLite eligibility and temporal boundaries, and gates that reject incomplete or poor reports.
Passing these tests establishes harness behavior; it does not make failing retrieval findings pass.
