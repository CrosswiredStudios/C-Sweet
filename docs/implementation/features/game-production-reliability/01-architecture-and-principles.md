# Architecture and design principles

## Goal

Add delivery feedback loops without weakening C-Sweet's existing trust model or turning current
large services into larger ones. New behavior should be expressed through small application
interfaces, domain-owned invariants, infrastructure adapters, and versioned transport contracts.

## Current architecture to preserve

The current orchestration model already provides the following guarantees:

- a sprint execution pins one immutable policy revision;
- assignment snapshots identify the exact principal responsible for each stage;
- a stage attempt has an idempotency key and explicit terminal status;
- policy controls transitions rather than agent-selected stage names;
- artifacts identify exact revisions and SHA-256 digests;
- capabilities and scoped resource grants must both authorize an agent action;
- untrusted agents run through certified Office isolation rather than host access.

The work in this plan adds evidence and projection layers around these guarantees. It does not
replace the orchestrator, authorization service, broker, artifact store, or memory engine.

## Target component boundaries

| Component | Responsibility | Must not do |
|---|---|---|
| Evaluation scenario catalog | Defines fixtures, expected invariants, and metrics | Dispatch production work directly |
| Evaluation runner | Provisions a disposable environment and invokes public application services | Reimplement orchestration transitions |
| Criterion coverage service | Validates ownership and evidence for stable criteria | Parse arbitrary agent prose |
| Finding ledger service | Owns finding state transitions and append-only history | Decide policy authorization itself |
| Effective contract builder | Combines pinned policy, assignment, schemas, and resolved grants | Grant new authority or query hidden credentials |
| Contract renderers | Render prompt, operator, SDK, and test representations | Recompute policy or authorization |
| Playbook service | Proposes, approves, archives, and recalls procedures | Create a separate vector database |
| Program registry/runner | Evaluates triggers, deduplicates, and emits proposals | Bypass normal work-item and approval services |
| Readiness probe coordinator | Aggregates side-effect-free health checks | Repair systems during a diagnostic request |

## Dependency direction

Use the existing Domain → Application → Infrastructure/API dependency direction.

1. Domain owns entities, value objects, statuses, and legal transitions.
2. Application owns use-case interfaces and orchestration between domain services.
3. Infrastructure owns EF Core, memory, clock, hashing, provider, and Office adapters.
4. API and broker handlers translate transport requests into application calls.
5. UI consumes response models and never derives authorization or lifecycle status locally.
6. Evaluation projects depend on public application/contracts surfaces and test doubles, not on
   private database mutations.

## Applying SOLID

### Single Responsibility Principle

Do not add criterion validation, finding transitions, contract rendering, and evaluation metrics to
`WorkOrchestrationService`. It should call narrow collaborators.

Illustrative boundaries:

```csharp
public interface ICriterionCoverageValidator
{
    Task<CriterionCoverageResult> ValidateAsync(
        WorkExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IReviewFindingLedger
{
    Task<ReviewFinding> RecordAsync(
        RecordReviewFinding command,
        CancellationToken cancellationToken = default);
}

public interface IEffectiveAssignmentContractBuilder
{
    Task<EffectiveAssignmentContract> BuildAsync(
        Guid stageExecutionId,
        CancellationToken cancellationToken = default);
}
```

The names are proposed and may be adjusted to local naming conventions, but each interface must
have one reason to change.

### Open/Closed Principle

- Model evaluation scenarios behind `IGameProductionEvaluationScenario`; add a scenario without
  editing the runner.
- Model program triggers and actions using registered handlers; add a trigger type without a
  switch statement that grows indefinitely.
- Render one effective contract through separate prompt, UI, and JSON renderers.

### Liskov Substitution Principle

Test doubles must preserve production semantics. For example, a fake finding repository must still
enforce optimistic concurrency and unique idempotency keys. A fake clock may control time but must
not skip expiry behavior.

### Interface Segregation Principle

Prefer small interfaces such as `IProgramTriggerEvaluator`, `IProgramDeduplicator`, and
`IProgramMaterializer` over one service exposing every program operation. Agent capabilities must
also remain task-oriented: read, propose/address, verify, and waive are separate grants.

### Dependency Inversion Principle

Domain/application code depends on abstractions for time, identity, hashing, storage, memory, and
provider calls. Infrastructure implementations depend on EF Core, the C-Sweet memory package, and
Office/provider clients. Tests inject deterministic implementations.

## Cross-cutting invariants

Every phase must preserve these rules:

1. **Tenant isolation:** every query is constrained by organization before resource lookup.
2. **Exact identity:** reviewer, assignee, approver, automation, and program identities are durable.
3. **Dual authorization:** installation capabilities and scoped resource grants must both pass.
4. **Pinned decisions:** policy, planning revision, assignment revision, artifact digest, and source
   commit are captured at the point of work.
5. **Idempotency:** retried mutations return the original result or a stable conflict.
6. **Optimistic concurrency:** state changes require the revision observed by the caller.
7. **Append-only evidence:** prior review and program evidence is never rewritten or discarded.
8. **Independent verification:** addressing a finding is distinct from verifying it.
9. **Deterministic authority:** model output may suggest an outcome; trusted code chooses legal
   transitions.
10. **No privilege expansion:** generated contracts and playbooks describe effective authority but
    cannot create it.

## Common workflow example

Suppose a vertical-slice work item has these criteria:

- `AC-001`: the player can move and jump with keyboard and controller;
- `AC-002`: the scene maintains 60 FPS on the approved test profile;
- `AC-003`: all actionable UI has a non-color status indicator.

The producer delegates `AC-001` to engineering, `AC-002` to technical art/QA, and `AC-003` to
UI/accessibility. Each stage receives only its claimed criteria and the evidence it may read. QA
records finding `FND-0042` against `AC-002` and build digest `sha256:...`. Engineering submits an
addressing record against a new build. QA verifies that exact build. The orchestrator then follows
the pinned `verified` transition; neither agent supplies a target stage.

## Error contract

New services should return stable categories suitable for APIs, MCP responses, tests, and UI:

| Category | Meaning | Example remediation |
|---|---|---|
| `validation` | Input is malformed or incomplete | Add a missing criterion reference |
| `conflict` | Revision or idempotency conflict | Refresh and retry with the latest revision |
| `authorization` | Effective grants do not allow the action | Request the named scoped grant |
| `precondition` | Valid input cannot be applied in the current state | Resolve blocking findings first |
| `dependency` | Office, provider, repository, or toolchain unavailable | Restore dependency and retry |
| `internal` | Unexpected trusted-platform fault | Use correlation ID for operator diagnosis |

Do not return secrets, raw provider responses, hidden prompts, or model chain-of-thought in error
details.

## Pull request sizing

A junior developer should submit one independently testable behavior per pull request. A good PR
adds one domain transition plus its persistence mapping, service method, transport mapping, and
tests. Avoid PRs that combine schema changes, UI, program execution, and memory promotion.
