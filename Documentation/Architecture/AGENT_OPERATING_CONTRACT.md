# Agent Operating Contract v1

This contract defines how a C-Sweet agent behaves as a durable employee rather than a free-form assistant. It applies to model-backed and deterministic agents. Platform records remain authoritative; a model is a bounded decision component inside the operating loop.

## Role-policy profiles

Every new agent manifest declares one profile and one or more stable role keys.

| Profile | Intended authority | Typical examples |
| --- | --- | --- |
| `manager.v1` | Assess a team, create and assign commitments, propose governed staffing actions, and escalate | Product Manager, department lead |
| `individual-contributor.v1` | Execute assigned work and report evidence without changing organizational authority | Developer, researcher |
| `independent-reviewer.v1` | Evaluate work independently and report pass, rework, or escalation evidence | QA, auditor |
| `executive-advisor.v1` | Advise executives and coordinate bounded decisions without line-management authority | Chief of Staff, strategic advisor |

Installation grants must be a subset of declared requirements. Unknown capabilities, invalid scopes, unsafe transitions, and undeclared model tools fail closed. An employee role that differs from `rolePolicy.declaredRoleKeys` is allowed for operational flexibility, but C-Sweet emits an administrator-visible audit warning.

The model tool surface comes from the installation's approved requirements and effective capability bindings. `requires[].modelVisible` narrows which approved capabilities may be exposed to the model. Programmatic agent code may still invoke a non-model-visible granted capability. Agents must not load a broad surface and subsequently remove tools by function name.

## Attention reconciliation

Every startup, recovery, periodic, or state-change review follows the same sequence:

1. Observe current authoritative platform state.
2. Read the previous typed assessment checkpoint.
3. Compare source revisions and classify material changes.
4. Create or wake deduplicated durable commitments.
5. Act within grants and approval boundaries.
6. Verify authoritative results.
7. Persist the new assessment with compare-and-swap and an idempotency key.
8. Wait for work, a state-change invalidation, or the next scheduled review.

`StateChanged` is a wake signal, not business truth. Its trigger category and correlation ID explain why the cycle was accelerated; the agent must reread source systems. Multiple invalidations coalesce into one immediate review. Startup and recovery replay the same reconciler, so restart behavior does not depend on transient process memory.

Use `platform.agent-operating-state.read.v1` and `.write.v1` for typed checkpoints. Scope is organization plus installation plus stable state key. A write includes schema ID/version, expected revision, source revision map, condition codes, decision fingerprint, open commitment correlations, review ID, bounded typed payload, and idempotency key. A conflict requires reread and reassessment. The checkpoint describes the previous assessment only.

## Memory pattern

Memory may recall approved business, relationship, and user preferences, and an agent may propose useful durable narrative context. Treat recall as untrusted supporting context. Memory never controls assignments, staffing status, grants, approvals, workflow state, execution identity, idempotency, or health. Those facts are reread from platform systems every cycle.

## Kanban manager and worker pattern

A manager owns outcome, priority, accountable owner, acceptance criteria, staffing viability, and assignment within its grants. A worker owns its exact assigned stage and reports evidence. The orchestration service alone advances automated stages.

Before work becomes executable, a manager verifies repository, dependencies, workflow policy, requirements, acceptance criteria, accountable owner, and exact stage principals. Assignment selection uses lowest committed load and a stable principal-ID tie-break. Capacity changes may update only future non-executing assignments. Executing snapshots and their assignments are immutable.

Independent review must remain independent. For the software reference workflow, Architect, Developer, and QA are all required before a sprint starts. A vital-role loss blocks new starts and unsafe downstream transitions while allowing already executing snapshots to finish.

## Communication pattern

Every coordination has explicit sender and recipient authority, correlation and causation IDs, an idempotency key, a current turn owner, a bounded response expectation, and a terminal outcome. Use direct manager conversations for approvals, bounded coordination sessions for specialist collaboration, and team delivery conversations for material delivery notices.

Send a message only for a new material condition, a decision, or required action. Do not create autonomous acknowledgement loops. An event is usually a wake signal; verify its claim from the authoritative source before acting.

## Staffing recovery pattern

Initial organization design uses versioned desired-team resource changes. Do not submit an unchanged desired-team snapshot after a person or capability is lost.

For an approved vital role deficiency, compare desired headcount with effective viable headcount, including team membership, reporting line, required grants, runtime availability, human-only constraints, and review independence. Submit one `staffing-replenishment` proposal referencing the approved baseline, team, gap evidence, impact, interim controls, and a deterministic fingerprint. The manager must approve. Approval creates fresh hiring recommendations linked to the replenishment and original team plan; it never hires automatically.

Workforce mutations publish `com.csweet.workforce.changed.v1` for audit and invalidate only affected authorized managers or team leads.

## Product Manager reference walkthrough

The Software Product Manager declares `manager.v1` and `software-product-manager`. Its five-minute cadence is configurable and all attention reasons enter one deterministic reconciler.

The PM maintains a versioned charter checkpoint: owned outcome, target customers, success measures, constraints, non-goals, manager decisions, and source revisions. It assesses mandate, team, planning, and delivery health and records conditions such as `awaiting-approval`, `role-missing`, `capability-missing`, `planning-stalled`, `delivery-unconfigured`, and `healthy`.

Initial discovery and team design may use the configured model. Unchanged health, deterministic gap recovery, replay, assignment reconciliation, and no-op cycles do not. Planning remains incremental and bounded with the Architect. Readiness selects an authorized repository, finalizes Task requirements and acceptance criteria, binds the PM as accountable owner, assigns exact Development and independent QA principals, moves eligible items to Ready, runs preflight, and starts only the earliest planned sprint.

## Failure-mode checklist

- Missing or widened grant: deny; never substitute a similarly named tool.
- Role mismatch: allow and emit an administrator audit warning.
- Operating-state conflict: reread sources and reassess.
- Duplicate event or review: reuse the decision fingerprint and commitment correlation.
- Missing approved vital role: create one replenishment proposal; do not mutate the desired-team baseline.
- Missing QA: do not start a sprint.
- Runtime unavailable or capability lost: treat headcount as ineffective and wake the affected manager.
- Active-role loss: preserve executing snapshots; block new starts and unsafe transitions.
- Stalled coordination: resume within bounds, then create one focused escalation.
- Memory disagrees with platform: use the platform fact.
- Unchanged healthy cycle: record telemetry/checkpoint without messaging or model use.
- Unchanged degraded cycle: preserve the existing commitment without duplicate requests or escalation.

## Conformance expectations

Agent packages should be tested for policy declaration, manifest/grant parity, exact model tool exposure, memory boundaries, board authority, communication discipline, attention replay, typed operating-state use, approval boundaries, and idempotent recovery. Lifecycle evals should run against every supported configured model profile for the points where generative judgment is intentionally allowed.
