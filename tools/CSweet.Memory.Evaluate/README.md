# Memory retrieval evaluation

`Corpus.SeedAsync` defines `employee-business-retrieval-v2`: six employee/business decisions,
source-linked claims and aliases, procedure/core context, valid-time supersession, forbidden
states and audiences, vector eligibility, identifiers, Unicode, abstentions, and near-neighbor
distractors. Seven calibration queries and 27 originally held-out queries remain separate in
every report. The 0.2.0 findings informed the 0.2.1 alias and ranking fixes, so this corpus now
provides regression evidence, not a blind quality estimate. Preserve its questions and expected
answers for before/after comparison; use a fresh independent set for future quality acceptance.

Build this standalone tool against the same memory packages that the application is testing.
For the current unpublished 0.3.0 feed, from the C-Sweet repository:

```powershell
dotnet restore tools/CSweet.Memory.Evaluate/CSweet.Memory.Evaluate.csproj --configfile tools/CSweet.Memory.Evaluate/NuGet.local.config
dotnet build tools/CSweet.Memory.Evaluate/CSweet.Memory.Evaluate.csproj -c Release --no-restore
dotnet tools/CSweet.Memory.Evaluate/bin/Release/net10.0/CSweet.Memory.Evaluate.dll sqlite artifacts/memory-retrieval-evaluation/0.3.0 1000 5
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
failure for forbidden released results or missing database instrumentation. Recall misses
remain measured findings; passing execution does not assert that quality release gates pass.

`EvaluationMetrics` reports weighted recall@10, irrelevant results, correct abstentions,
forbidden results, warm p50/p95 search latency, database executions and rendered context size.
Candidate eligibility and escaping compile the actual platform `MemoryRecallPolicy`; packing
is explicitly an evaluation policy with a 2,048-character rendered allowance. It does not
claim end-to-end platform invocation budget enforcement. Search timing includes source reads
and excludes packing/model calls. Full JSON retains individual samples and fixture IDs.
Reports fingerprint all six compiled source files (including the linked recall policy), the
evaluator assembly, scenarios and store assembly, and record runtime and corpus configuration.

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
