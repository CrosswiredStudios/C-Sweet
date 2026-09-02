# Phase 1 - Golden-path game-production evaluations

## Goal

Create a deterministic harness that exercises C-Sweet's real work orchestration with a small game
project. This phase should expose workflow defects before the later phases add new behavior.

An evaluation is not a unit test for an agent prompt. It is a controlled delivery run with pinned
inputs, a disposable organization, observable state, and explicit pass/fail invariants.

## Why this phase comes first

Without a baseline, later improvements cannot show whether they made delivery safer or merely more
complex. The harness also becomes the acceptance environment for criteria, findings, generated
contracts, playbooks, and programs.

## Proposed project layout

Prefer a separate non-production test project, for example:

```text
tests/
  CSweet.GameProduction.Evaluations/
    Scenarios/
    Fixtures/
    Assertions/
    Metrics/
    Doubles/
```

Keep reusable generic evaluation infrastructure separate from game-specific scenario definitions.
If generic infrastructure grows enough to serve other business profiles, it can later move to a
shared `CSweet.Evaluations` project without changing scenario contracts.

## Core abstractions

Use small interfaces so test environments can substitute dependencies without changing scenarios.

```csharp
public interface IEvaluationScenario
{
    string Key { get; }
    Task<EvaluationExpectation> ArrangeAsync(
        IEvaluationEnvironment environment,
        CancellationToken cancellationToken);
}

public interface IEvaluationRunner
{
    Task<EvaluationResult> RunAsync(
        IEvaluationScenario scenario,
        EvaluationRunOptions options,
        CancellationToken cancellationToken);
}

public interface IEvaluationInvariant
{
    string Key { get; }
    Task<InvariantResult> EvaluateAsync(
        EvaluationObservation observation,
        CancellationToken cancellationToken);
}
```

The scenario arranges data and expected outcomes. The runner controls lifecycle and timeouts. Each
invariant inspects one concern. Metrics collect observations but do not decide workflow state.

## Deterministic environment

Each run must create and later dispose of:

- a unique organization and owner;
- a board with a published orchestration policy;
- a sprint and work-item hierarchy;
- exact game specialist installations and grants;
- a temporary source repository at a known commit;
- a minimal Godot fixture with deterministic build/test commands;
- test-specific provider and clock configuration;
- a correlation ID shared across work, inference, Office, artifact, and audit records.

Never share an organization, board, repository branch, memory namespace, or idempotency key between
parallel runs.

### Fixture rules

- Store fixture source in the repository or a versioned test package; do not clone an unpinned URL.
- Keep the Godot project tiny enough to execute locally and in CI.
- Seed deterministic validation scripts for non-subjective requirements.
- Use a fake model only for orchestration failure tests. At least one opt-in profile must exercise
  real supported models and real specialist prompts.
- Freeze time through the application's clock abstraction when asserting durations, retries, or
  expiry.

## Required scenarios

### Scenario A - Vision to approved design package

Flow:

1. Creative Director creates the project vision.
2. Game Designer, Narrative Designer, Art Director, Audio Designer, and UI/Accessibility Designer
   create role-owned artifacts from the pinned vision revision.
3. Producer assembles an artifact package.
4. Creative Director reviews the exact package revision.

Assertions:

- all required artifact types exist once;
- every package member points to an accepted immutable revision;
- the approval references the exact package digest;
- no specialist reads an artifact outside its grants;
- replaying a completion callback creates no duplicate artifact or transition.

### Scenario B - Cross-discipline vertical slice

Flow:

1. Producer decomposes one approved feature into discipline-specific children.
2. Specialist stages execute in the policy-defined order or parallelization groups.
3. Engineer integrates approved inputs through the Godot toolchain.
4. Build/Release creates an immutable build record.

Assertions:

- every child has an accountable owner and exact installation assignment;
- dependency stages do not start early;
- the final build is derived from the expected commit and artifact digests;
- no agent receives source, tools, or network access beyond effective grants.

### Scenario C - QA/playtest revision loop

Flow:

1. QA validates an exact build and records a blocking defect.
2. The implementation stage receives the finding and produces a new build.
3. QA verifies the new build and Playtest Researcher records usability evidence.
4. The policy advances to approval only after all blocking findings are verified.

Assertions:

- the original defect remains queryable;
- evidence identifies both rejected and corrected build digests;
- the implementer cannot verify its own fix;
- the number of traversals does not exceed policy;
- a stale QA result for the first build is ignored.

Phase 1 may represent findings through existing outcome JSON. Phase 2 replaces that temporary
assertion path with first-class finding records.

### Scenario D - Recovery and idempotency

Inject one fault at a time:

- process restart after dispatch but before acknowledgement;
- duplicate dispatch;
- duplicate outcome;
- outcome after cancellation;
- retry after provider rate limit;
- expired assignment revision;
- Office interruption after a side effect but before completion.

Assert one durable side effect, the correct ignored/conflict result, bounded retries, and an audit
record containing the correlation and idempotency keys.

### Scenario E - Dependency and security failures

Test unavailable Office, unavailable provider, missing Godot toolchain, missing capability, missing
board grant, wrong organization, and digest mismatch. Each failure must be classified, must avoid
advancing the stage, and must avoid leaking credential or host-path data.

## Metrics

Collect these metrics for every run:

```json
{
  "scenario": "game.vertical-slice.v1",
  "terminalStatus": "Completed",
  "elapsedMilliseconds": 182400,
  "stageAttempts": 11,
  "retryCount": 1,
  "revisionCycles": 1,
  "duplicateSideEffects": 0,
  "criterionCoveragePercent": 100,
  "evidenceCompletenessPercent": 100,
  "humanGateCount": 2,
  "inputTokens": 42100,
  "outputTokens": 8700,
  "estimatedCost": 1.42
}
```

Cost and token assertions should use budgets/ranges, not exact equality. Security, identity,
digest, transition, idempotency, and evidence assertions must be exact.

## Optional subjective judge

A model judge may score artifact usefulness, consistency, or playability only after deterministic
checks finish. Store the judge model, prompt version, input digests, raw score, and rationale. Mark
the result nondeterministic and never let it convert a deterministic failure into a pass.

## Implementation slices

### Slice 1.1 - Harness shell

- Add scenario, runner, invariant, observation, and result abstractions.
- Add a fake scenario proving setup, execution, timeout, observation, and cleanup.
- Emit machine-readable JSON plus a concise console summary.

### Slice 1.2 - Disposable platform fixture

- Provision organization, board, sprint, policy, installations, and grants through application
  services or public test fixtures.
- Add deterministic cleanup and parallel-run isolation.
- Prove one run cannot query another run's data.

### Slice 1.3 - Minimal Godot fixture

- Add the pinned project fixture and deterministic validation command.
- Exercise the normal Godot plugin/broker path.
- Record source commit, build digest, and toolchain identity.

### Slice 1.4 - Core scenarios

- Add scenarios A through E one at a time.
- Require a passing scenario and documented metrics before adding the next.

### Slice 1.5 - Real-model profile

- Add an explicit opt-in category for supported model profiles.
- Keep deterministic CI green when credentials or models are unavailable.
- Publish comparative metrics without declaring model prose authoritative.

## Tests and acceptance criteria

- A harness unit test proves cleanup runs after success, failure, cancellation, and timeout.
- Parallel runs use disjoint organization and repository identities.
- A failing invariant names expected, actual, and evidence references.
- Scenario results are reproducible with the deterministic profile.
- No evaluation helper directly changes an orchestration status in the database.
- The five required scenarios execute through the same application services used in production.
- A real-model run can be selected by profile without changing scenario source.

## Completion gate

Do not begin Phase 2 until the baseline result is checked in as documentation or a test artifact and
the recovery scenario demonstrates no duplicate side effects.
