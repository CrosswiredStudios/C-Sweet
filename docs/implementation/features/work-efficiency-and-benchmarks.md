# Work efficiency and business benchmarks

## Entry points

`WorkEfficiencyPanel` appears under Analytics → Work efficiency, a project's Efficiency
tab, and a work item's detail. Owners and managers can inspect direct and descendant
usage, lifecycle durations, paginated inference activity, and CSV exports. The report
uses the existing Workstream → board → epic/story/task hierarchy.

`Benchmarks.razor` is linked from Enterprise. `BenchmarkEndpoints` requires the host
administrator policy. Its wizard defines a shared goal, initial reporting structure,
document inputs, two model variants, role overrides, assistance policy, rubric, optional
judge, repetitions, and scheduling. The API accepts up to ten variants. Starting a campaign
creates real businesses and consumes configured provider capacity. The comparison shows
every trial, completion counts, delivery median/range, reported usage coverage, checks,
and separate human/judge scores. Partial rubric scores are not presented as final scores.

## Metric semantics

- `AgentRunLog` is the inference receipt. New provider attempts record a start and update
  the same ID at completion, failure, or cancellation. Streaming usage updates are not
  additional calls. `InferenceMeasurements.ProviderCalls` excludes queue bookkeeping and
  failures before provider dispatch. Attempts with no final usage remain visible.
- `ReportedInputTokens` and `ReportedOutputTokens` retain 64-bit provider counts. Total
  tokens are their sum; cached input and reasoning are subsets and are not added again.
  Missing usage is represented by coverage counts, not a zero-token claim.
- `InferenceAttribution.CaptureAsync` captures authenticated leased work, work-item
  ancestors, project, package version, and configuration digest. TaskRun links are an
  authoritative fallback. Reparenting later does not move recorded descendant usage.
  Unlinked, organization-scoped calls are business overhead; legacy unknown ownership
  remains visibly unknown. Direct synchronous calls without leased-work context cannot
  be attributed to an individual work item.
- `UsageCapturingChatClient` accounts for every memory-enrichment call separately and
  records cumulative streaming usage once. The platform runner also records planning
  and workflow calls. Background enrichment is included in business consumption.
- `WorkLifecycleEvent` is append-only and committed with work status changes. Lead time
  is creation to final completion; cycle time is first start to final completion;
  waiting time sums recorded waiting intervals. Reopening is counted. These are lifetime
  measures; the usage date filter does not change them. Parent duration is not the sum
  of parallel children. Historical gaps are explicit.
- Business reports cap detailed rows at 50,000 and mark truncation; campaign totals
  aggregate in the database. Ordinary inference retains existing availability behavior
  if its telemetry store fails. Benchmark broker requests require the initial durable
  receipt before provider dispatch. A crash can leave a started call with unknown usage.

## Campaign execution

`BenchmarkDefinition` is immutable and versioned. Its manifest records the blueprint,
pinned agent package IDs/digests, configuration/resource/grant digests, and platform build
version. Provisioning rejects a changed initial agent configuration or unavailable pinned
version. Credentials are not exported. Creating a new version is required to revise a
definition. Launch keys are scoped to the administrator; reusing a key with different
parameters fails. Execution order is randomized within each repetition and persisted.

`BenchmarkService.AdvanceAsync` consumes transactionally committed `BenchmarkWake` hints
and bounded recovery discovery. PostgreSQL advisory locks coordinate admission across
replicas. Ineligible sequential trials are excluded before the dispatcher batch limit.
The platform worker performs recovery; agents do not poll benchmark state. Provisioning
shares one transaction and savepoint; failures roll back the business before the trial
is marked failed. Existing platform limits still apply; there is no benchmark delivery
deadline or benchmark token budget.

Each trial gets an isolated organization, agent installations, empty initial memory,
normal business source-control defaults, a project, and copied document inputs. One goal
turn is released to the initial lead. `BenchmarkModelPolicy` applies the variant default
and role overrides when effective configuration is resolved, including later hires.
Workforce changes and unexpected reported model identifiers appear as deviations.

Assisted delivery allows human actions; approvals-only restricts mutation routes to
decisions. Unattended delivery rejects human mutation routes and human messages at the
communications service boundary. `BenchmarkHumanPolicyMiddleware` also resolves legacy
task/artifact routes by persisted ownership. Platform administrators can still change
global infrastructure; runs are not a sandbox against a malicious administrator.

The first recorded project completion after goal release ends delivery timing, even if
the project is reopened before recovery. New claims are refused after that declaration.
The worker disables schedules/installations and cancels queued/leased work; already
executing external operations may finish. Setup, delivery, evaluation, and trailing
inference have separate totals. Cancellation retains partial results and is never a
successful completion. Human messages, agent request-response messages, and non-LLM MCP
tool attempts are separate counters; they overlap semantically and are not summed into
model calls. Tool counters follow audit delivery and can lag. They do not currently
measure every approval, correction, or internal handoff.

## Evaluation and evidence

`SnapshotAsync` freezes artifact revision content/digests as of declaration and matching
source-control validation receipts with commit SHAs. It marks incomplete capture rather
than silently evaluating a truncated product. The snapshot has bounded document and
validation counts; unresolved work is included. Artifact titles and membership are read
at capture, so delivery metadata can still change between declaration and recovery.

`EvaluateAsync` runs document existence/contains checks and optionally checks recorded
source-control validation outcomes. These checks are **not an independent executable
acceptance-test run**. The optional judge sees the frozen documents and rubric without
variant names, has no tools, and records its own token usage. Invalid output or interrupted
evaluation becomes Incomplete; an uncertain provider call is not automatically repeated.
Human review is append-only, requires a rationale, and remains separate from judge scores.
The UI/export computes weighted rubric means only after every rubric criterion is scored.

CSV contains delivery, coverage, interactions, phase usage, checks, and quality scores.
JSON also exports the immutable manifest, digest, frozen evidence, and assessments.
Business efficiency responses do not expose prompts, raw request evidence, or secrets.

## Deployment and verification

`WorkEfficiencyAndBenchmarks` adds inference attribution fields, lifecycle history,
definitions, campaigns, trials, assessments, and wake records. Existing logs default to
Legacy/Unknown. The migration backfills only a verified existing TaskRun → WorkTask link;
it does not infer historical parents/projects or invent lifecycle transitions.

Targeted verification lives in `WorkEfficiencyTests`, `BenchmarkTests`,
`AnalyticsEndpointTests`, and `AgentRunnerIntegrationTests`, plus existing inference,
memory, configuration, inbox, and communications regression tests. `BenchmarkPostgresTests`
uses `CSWEET_EFFICIENCY_TEST_POSTGRES` and requires a database name starting with
`efficiency_validation`; it runs migrations, concurrent idempotent launches, 64-bit SQL
aggregation, and sequential recovery with more waiting trials than one dispatcher batch.

The broader benchmark plan still needs independent Office/toolchain acceptance-test
execution, arbitrary repository/memory seed snapshots, atomic capture of all deliverable
types at declaration, full intervention/handoff attribution, and live agent/Office
end-to-end certification. This implementation does not claim those capabilities.
