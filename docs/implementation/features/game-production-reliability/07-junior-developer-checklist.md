# Junior developer delivery checklist

Use this document to create issues and pull requests. Keep PRs small, preserve dependency direction,
and complete the tests for a slice before starting the next slice.

## Before starting any issue

- [ ] Read the feature README and architecture document.
- [ ] Identify the authoritative existing service/model; do not create a parallel subsystem.
- [ ] Search for nested `AGENTS.md` instructions in every repository you will edit.
- [ ] Write the expected domain invariant and failure behavior in the issue.
- [ ] Identify capability and scoped-grant requirements for every new operation.
- [ ] Identify organization/resource filters for every query.
- [ ] Decide the idempotency key and optimistic concurrency behavior for every mutation.
- [ ] List package repositories and downstream consumers affected by public contracts.
- [ ] Confirm the PR can be tested and rolled back independently.

## Phase 1 - Golden-path evaluations

### Harness shell

- [ ] Add the evaluation test project and scenario/result abstractions.
- [ ] Add deterministic clock, ID, provider, and environment doubles.
- [ ] Add cleanup-on-success/failure/cancellation/timeout tests.
- [ ] Add JSON result output and a short human-readable summary.

### Disposable fixture

- [ ] Provision organization, owner, board, sprint, policy, agents, and grants through supported
      services/fixtures.
- [ ] Use unique correlation and idempotency namespaces per run.
- [ ] Prove parallel runs cannot read each other's data.
- [ ] Prove cleanup does not delete another run's resources.

### Godot fixture and scenarios

- [ ] Add a small pinned Godot project fixture.
- [ ] Record source commit, build digest, toolchain identity, and evidence.
- [ ] Add vision-to-package scenario.
- [ ] Add cross-discipline vertical-slice scenario.
- [ ] Add QA/playtest revision-loop scenario.
- [ ] Add restart/replay/idempotency scenario.
- [ ] Add dependency/security-failure scenario.
- [ ] Add optional real-model profile without making normal CI credential-dependent.

### Phase 1 gate

- [ ] Deterministic scenarios are reproducible.
- [ ] Duplicate side-effect count is zero.
- [ ] No helper mutates orchestration status directly in the database.
- [ ] Baseline metrics and known failures are documented.

## Phase 2 - Criteria and findings

### Criteria and coverage

- [ ] Add stable criterion entity/value model.
- [ ] Add ownership/contribution/verification coverage claims.
- [ ] Add EF mapping, constraints, indexes, and migration.
- [ ] Create first-class criteria for newly approved planning revisions.
- [ ] Add coverage report and uncovered-criterion preflight errors.
- [ ] Add tenant-isolation and concurrency tests.

### Finding ledger

- [ ] Add finding projection and append-only transition records.
- [ ] Implement Open → Addressed → Verified/Waived rules.
- [ ] Implement failed verification as reopening the same finding.
- [ ] Add idempotency and expected-revision enforcement.
- [ ] Add separate query and command application services.
- [ ] Add artifact-review and QA adapters.
- [ ] Retain exact evidence and source payload provenance.

### V2 contracts and capabilities

- [ ] Add V2 assignment/outcome records without modifying V1 positional contracts.
- [ ] Add criterion and finding transport records.
- [ ] Add separate read/address/verify/waive capabilities.
- [ ] Add explicit installation contract negotiation.
- [ ] Update broker and typed SDK adapters.
- [ ] Increment `CSweet.WorkManagement.Contracts` minor version.
- [ ] If changed, increment `CSweet.Agent.SDK` minor version.
- [ ] Update documented/template/test versions and downstream pins.
- [ ] Build, test, and pack changed packages.
- [ ] Verify consumers with sibling project references disabled.

### Phase 2 gate

- [ ] Every active criterion has an owner and verification path.
- [ ] Blocking findings prevent success until verified or properly waived.
- [ ] Implementers cannot verify their own fixes without explicit independent authority.
- [ ] Scenario C uses first-class findings and passes restart/replay tests.
- [ ] V1 compatibility tests pass.

## Phase 3 - Effective assignment contracts

- [ ] Add the canonical internal model.
- [ ] Add narrow readers for policy, assignment, grants, descriptors, criteria, findings, and
      evidence.
- [ ] Fail closed on missing or inconsistent inputs.
- [ ] Canonicalize ordering and compute SHA-256 digest.
- [ ] Persist digest with dispatch and validate it on outcome.
- [ ] Add agent-context renderer.
- [ ] Add authorized operator/diagnostic renderer.
- [ ] Add UI projection using server-provided allowed actions.
- [ ] Add lifecycle documentation/fixture generator and CI check mode.
- [ ] Add graph, outcome, schema, and capability consistency validators.
- [ ] Integrate the shared game AgentKit after parity tests.

### Phase 3 gate

- [ ] Identical pinned inputs produce identical bytes and digest.
- [ ] All renderers agree on stage, criteria, findings, capabilities, and outcomes.
- [ ] Unauthorized/unavailable tools never appear as effective.
- [ ] Stale contract outcomes cannot advance work.
- [ ] Generated-file drift fails CI with a clear remediation command.

## Phase 4 - Playbooks and programs

### Playbooks

- [ ] Add candidate lifecycle and evidence validation.
- [ ] Add authorized approve/reject/archive/supersede operations.
- [ ] Store approved procedures through existing memory APIs.
- [ ] Add scoped role/workstream/applicability recall.
- [ ] Enforce trust, confirmation, sensitivity, and token-budget filters.
- [ ] Record supplied/accepted/corrected/rejected usage feedback.

### Programs

- [ ] Add versioned definition, occurrence, and execution records.
- [ ] Create dedicated program identities with no default grants.
- [ ] Add trigger-handler, deduplicator, explorer, policy, and materializer interfaces.
- [ ] Implement `schedule.v1` and one report-only materializer.
- [ ] Add unique occurrence key and replay behavior.
- [ ] Add normal-service adapters for findings/work/artifacts/notifications.
- [ ] Prevent recursive program-trigger loops.
- [ ] Add five game program templates, disabled by default.
- [ ] Add observe-only mode.

### Phase 4 gate

- [ ] Unapproved playbooks cannot enter assignment context.
- [ ] Memory scope/sensitivity tests pass.
- [ ] Trigger replay produces exactly one occurrence/output.
- [ ] Grant revocation blocks subsequent program activity.
- [ ] Observe-only programs have no material side effects.

## Phase 5 - Readiness and rollout

- [ ] Add isolated readiness probes and coordinator.
- [ ] Add stable, non-secret remediation codes.
- [ ] Add evaluation, coverage/finding, assignment-contract, playbook, and program operator views.
- [ ] Add bounded-cardinality metrics and correlated structured events.
- [ ] Add independent rollout flags; do not use them as authorization.
- [ ] Run criteria/findings in shadow/dual-write mode.
- [ ] Canary V2 with selected first-party agents.
- [ ] Pilot playbook approval in one test workstream.
- [ ] Pilot one program in observe-only mode.
- [ ] Rehearse flag-based rollback without deleting records.
- [ ] Complete security review and operator documentation.

## Review checklist for every PR

- [ ] The PR has one primary reason to change.
- [ ] Domain rules are not hidden in controllers, UI, EF configurations, or prompts.
- [ ] Interfaces are small and consumers depend on abstractions.
- [ ] No large switch was introduced where registration/strategy is appropriate.
- [ ] No direct database write bypasses an existing application service.
- [ ] Organization filtering happens before resource details are returned.
- [ ] Authorization is checked server-side at the time of action.
- [ ] Mutations are idempotent and concurrency checked.
- [ ] Evidence uses exact immutable references.
- [ ] Logs exclude secrets and model chain-of-thought.
- [ ] Unit tests cover legal and illegal transitions.
- [ ] Integration tests cover persistence, authorization, and tenant isolation.
- [ ] End-to-end evaluation covers the changed behavior.
- [ ] Documentation and examples match the implemented wire shape.
- [ ] Package versions/pins are synchronized when applicable.

## Suggested pull request template

```markdown
## Behavior delivered

Describe one observable behavior and why it belongs in this layer.

## Invariants

- Invariant enforced:
- Idempotency behavior:
- Concurrency behavior:
- Authorization and tenant scope:

## Compatibility

- Existing contract behavior:
- Feature flag/shadow behavior:
- Rollback behavior:

## Verification

- Unit tests:
- Integration tests:
- Evaluation scenario:
- Manual check:
- Package build/test/pack and downstream verification, if applicable:
```

## Final initiative acceptance

- [ ] All phase completion gates pass.
- [ ] Deterministic game-production evaluations pass in CI.
- [ ] Real-model profile results are recorded for each supported profile selected for release.
- [ ] No security/isolation invariant was weakened.
- [ ] V1 compatibility and V2 negotiation are documented.
- [ ] Rollout and rollback were exercised in a production-like environment.
- [ ] Programs remain project opt-in and start with no grants.
- [ ] The implementation contains no copied RoboCo source.
