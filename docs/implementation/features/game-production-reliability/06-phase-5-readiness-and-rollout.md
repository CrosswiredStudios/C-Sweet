# Phase 5 - Operator readiness and controlled rollout

## Goal

Make the new behavior understandable and diagnosable before it becomes the default. Operators
should be able to answer: Is the platform ready? What contract governed this work? Why is it
blocked? Which exact evidence was reviewed? What automated program caused this proposal?

## Readiness diagnostics

Add one application-level readiness coordinator that calls side-effect-free probes. It should
report each dependency independently instead of returning one generic healthy/unhealthy flag.

Required probes:

- C-Sweet API and database connectivity/migration state;
- Agent Host/broker connectivity;
- certified Office enrollment, version, policy, and capacity;
- configured model profile connectivity and required capabilities;
- installed game-specialist availability and declared contract versions;
- effective board/workstream grants for the selected workflow;
- source-control provider and repository access through trusted adapters;
- Godot toolchain/plugin version and supported operations;
- artifact store and review worker readiness;
- memory store availability and scoped recall capability;
- program runner state and delayed/failed occurrence counts.

Probe output example:

```json
{
  "key": "godot-toolchain",
  "status": "Degraded",
  "summary": "Office is reachable but the required Godot profile is unavailable.",
  "details": {
    "requiredProfile": "godot-4.5-headless",
    "officeId": "office-guid"
  },
  "remediationCode": "install_or_assign_toolchain_profile",
  "checkedAt": "2026-08-31T18:00:00Z"
}
```

Do not include credentials, secret locations, host filesystem paths, or raw connection strings.

## Operator views

### Evaluation runs

Display scenario/profile, pinned fixture version, status, invariant failures, metrics, correlation
ID, and downloadable machine-readable result. Subjective judge results must be visually labeled
nondeterministic.

### Work-item coverage and findings

Display:

- criteria and owner/contributor/verifier coverage;
- uncovered criteria before execution;
- current findings grouped by status/severity;
- append-only finding transition history;
- exact artifact/source/build evidence;
- actor, grant revision, policy revision, and idempotency/correlation IDs where appropriate.

The UI asks services for `AllowedActions`; it does not infer permissions from status or role.

### Effective assignment contract

Show the immutable contract digest, pinned inputs, effective capabilities/scopes, preconditions, and
legal outcomes. Provide a copy/download function for diagnostics after authorization. Never show
secret values or hidden prompt internals.

### Programs and playbooks

Display enabled state, version, opt-in scope, identity, grants, last occurrence, deduplication key,
last output, approval mode, observe-only state, and failures. Playbooks show scope, evidence,
approval history, current version, and usage feedback.

## Observability

Use structured events and metrics with bounded-cardinality dimensions.

Recommended metrics:

- evaluation run duration and result by scenario/profile;
- criterion coverage percentage at preflight;
- open/aging blocking findings;
- finding address-to-verify duration and reopen count;
- assignment contract build failures and digest conflicts;
- playbook candidates/approvals/uses/corrections;
- program occurrences, deduplicated occurrences, proposals, approvals, and failures;
- readiness probe result and duration.

Do not use organization, work item, finding, commit, build digest, or correlation ID as metric label
values. Put those identifiers in structured logs/traces.

Every cross-system operation should carry the existing correlation/causation model through work,
Office, provider, artifact, review, memory, and program events.

## Feature flags and compatibility

Use independent flags so rollout can stop at a safe boundary:

| Flag | Default initially | Effect |
|---|---|---|
| `GameEvaluationsEnabled` | Off outside test/admin environments | Allows evaluation runs |
| `WorkCriteriaLedgerEnabled` | Off | Enables first-class criteria/coverage writes |
| `WorkFindingLedgerEnabled` | Off | Enables finding ledger and dual-write adapters |
| `EffectiveAssignmentV2Enabled` | Off | Negotiates V2 assignments for capable agents |
| `PlaybookPromotionEnabled` | Off | Allows proposal/approval lifecycle |
| `ProductionProgramsEnabled` | Off | Allows program scheduler; definitions remain separately disabled |

Flags are rollout controls, not authorization. A subject still needs all capabilities and scoped
grants when a flag is enabled.

## Rollout sequence

### Stage 1 - Development baseline

- Run Phase 1 deterministic scenarios on every relevant change.
- Establish baseline metrics and known failures.
- Keep all new persistence and runtime flags off.

### Stage 2 - Criteria/findings shadow mode

- Create first-class records alongside existing JSON outputs.
- Compare projections and report mismatches.
- Read existing behavior for decisions until parity is demonstrated.

### Stage 3 - V2 assignment canary

- Enable V2 only for explicitly selected first-party game agents declaring support.
- Record contract digest/parity telemetry.
- Retain V1 dispatch fallback for installations that do not declare V2.

### Stage 4 - Playbook approval pilot

- Permit candidate creation and manager approval for one test workstream.
- Recall approved procedures in shadow mode first and compare intended context.
- Enable assignment injection after access and token-budget tests pass.

### Stage 5 - Programs observe-only

- Enable one program for one test workstream without materialization.
- Compare proposals with human assessment and tune deterministic thresholds.
- Enable proposal/finding creation only after deduplication and authorization evidence is clean.

### Stage 6 - General availability

- Make first-class criteria/findings and effective contracts the normal first-party path.
- Keep V1 compatibility until a separately announced removal release.
- Programs remain project opt-in; never enable them globally by migration.

## Rollback behavior

- Disabling a flag stops new behavior but does not delete records.
- Existing findings remain readable and their blocking effect follows the pinned execution policy.
- In-flight V2 attempts finish under their pinned contract; do not silently downgrade them to V1.
- Program disable prevents new occurrences and materialization; already materialized work remains
  normal work with full provenance.
- Playbook disable stops new injection but retains approved procedures and feedback history.
- Database migrations are forward-safe; rollback uses feature flags and application deployment,
  not destructive down migrations in production.

## Security review checklist

- Threat-model every new read and mutation by subject, scope, and resource enumeration risk.
- Verify program identities start with no grants.
- Verify playbook scope and sensitivity filters before ranking or rendering content.
- Verify finding evidence access independently from finding metadata access.
- Verify readiness probes reveal no secrets or host details.
- Verify generated contracts describe authority without serving as bearer tokens.
- Verify no phase adds Docker socket, host credential, broad filesystem, or unrestricted network
  access to agents.
- Verify audit records contain authorization decisions without recording private reasoning.

## Documentation and support

Before enabling each flag:

- update the work-management feature documentation;
- document new capabilities and examples for agent authors;
- document contract negotiation and package compatibility;
- add operator remediation entries for every readiness/error code;
- add release notes and migration/rollback notes;
- include the exact commands used to build, test, and pack changed packages.

## Tests and acceptance criteria

- A failing probe does not prevent unrelated probes from returning results.
- Readiness endpoints enforce organization/operator authorization.
- Logs/traces connect an evaluation or program occurrence across all involved services.
- Metric labels remain bounded and contain no tenant-specific identifiers.
- Each feature flag disables new side effects cleanly.
- In-flight work remains governed by its pinned policy and effective contract during flag changes.
- V1 and V2 agents can execute concurrently.
- Program observe-only mode cannot materialize domain records.
- Rollback retains all audit, criterion, finding, playbook, and program history.

## Completion gate

General availability requires passing deterministic game-production evaluations, package
compatibility verification, security review, operator documentation, an observe-only program
pilot, and a successful rollback rehearsal.
