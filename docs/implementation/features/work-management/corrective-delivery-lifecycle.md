# Corrective delivery lifecycle and agent collaboration

## Objective and tracking

Make every accepted rejection lead to durable, authorized corrective work or a
specific actionable planning decision. Owner-report generation must never be a
prerequisite for assignment, execution, recovery or independent verification.

Status: implementation in progress. Update this register with code symbols,
verification evidence and rollout prerequisites as each increment completes.
Local builds do not update installed agents. Preserve published historical
packages, accepted work, exact-source evidence and existing authority boundaries.

| Increment | Outcome | Status |
| --- | --- | --- |
| 1. Corrective lifecycle | Rejection persists an owned, recoverable correction handoff atomically; supports release findings after the original sprint closes | Implemented and tested; live rollout pending |
| 2. Reporting separation | Corrective coordination runs independently of owner prose; fallback reports cannot suppress pending work or manufacture progress | Implemented and tested; live rollout pending |
| 3. Findings ledger | Criterion/severity/source/evidence fields; addressed and independently verified states; complete revision history | Planned |
| 4. Handoff contracts | Explicit required evidence and dependency validation before activation; compact authorized assignment packets | Planned |
| 5. Shared execution | Common persistence/finalization/recovery mechanics across ticket and aggregate workers; role-specific judgement remains separate | Planned |
| 6. Proportional planning | Small playable increments, bounded near-term refinement, distinct risk-based reviews, exact-input evidence reuse | Planned |
| 7. Progress and recovery | Cross-lifecycle progress receipts, bounded repeated-failure handling and current-state reconnect discovery | Planned |

## Architectural rules

- The platform owns transitions, durable handoffs, idempotency and notification
  outbox writes. Agents exercise judgement within approved scope and grants.
- An accepted QA rejection is successful review execution with a negative product
  result. Transport, invalid test harness, missing evidence and permission waits
  retain distinct recovery paths; none silently becomes a product pass.
- Every open finding has an accountable next action. Routine in-scope correction
  uses existing approved authority; new scope, spending, staffing or permissions
  require the existing governed decision/planning paths.
- A corrective task links the rejected candidate and findings. Completed source
  tasks and their sprint history remain immutable. A closed implementation sprint
  must not strand later release findings.
- Duplicate or reordered notifications are wake hints. Current authorized reads
  determine work. Persist authoritative state and outbox records atomically;
  reconnect discovery is bounded and resulting effects are idempotent.
- Execution and reporting have separate outcomes. Reporting failure cannot erase
  a successful handoff, return ownership to nobody, or count as corrective progress.
- A developer's claim of a fix is not independent verification. Retain candidate,
  criteria, environment/toolchain and reviewer bindings on evidence.
- Active workflow graphs are not silently rewritten. Follow-up planning preserves
  historical graph revisions and makes changed dependencies visible.

## First implementation acceptance

- [x] Accepted release rejection creates/reuses a durable correction handoff in
  the same transaction as findings, even with no report model available.
- [x] Assigned manager can discover and coordinate retained correction work after
  restart, missed notifications and an agent package update.
- [x] Correction operates after the implementation sprint is completed without
  reopening historical accepted task #38 or falsifying its verdict.
- [x] Reports summarize actual work receipts; fallback/report caching cannot
  suppress execution or indefinitely promise an unassigned investigation.
- [x] A single successful correction links its real engineering work and resumes
  acceptance through a linked follow-up graph with required independent validation.
- [x] Duplicate/reordered wakes, stale revisions, revoked authority and a missing
  engineer cannot create duplicate assignments or unauthorized changes.
- [x] Tests deliberately fail reporting and interrupt handoffs before/after
  persistence; recovery retains exactly one authorized next action.
- [x] Package-only consumer tests, package metadata, self-tests and agent bundles
  pass. Final release versions, notes and deployment sequence are recorded below.

## Remaining increments

### Findings ledger

Extend existing `WorkDeliveryFinding` rather than introduce a second store.
Record expected/actual behaviour, criterion, severity, source location and evidence
references. Link addressing commits and separate addressed from verified status.
Reviewers receive prior findings and verify each against the current candidate.

### Handoff contracts and shared execution

Declare required evidence as typed producer/consumer dependencies, rejecting
backward dependencies such as QA requiring technical-review evidence produced
later. Each worker receives current source identity, exact criteria, open
findings, bounded receipts and permitted next actions. Centralize shared runtime
mechanics without eliminating role-specific judgement or independent review.

### Proportional process and meaningful progress

Refine only the next playable increment deeply. Add reviews for distinct risks,
avoid duplicating native review work in separate tickets, and reuse evidence only
when all of its relevant input bindings remain valid. Track real assignments,
new diagnostic receipts, corrected candidates and verified findings as progress;
new prose, repeated unchanged reads and status oscillation are not progress.

## Prism Break recovery

At the starting checkpoint, QA 5.5.3 has an accepted `changes_requested` verdict
on workflow ticket #43. Finding `f178bf24-160c-4fac-a357-b30be464ba13`
records `SAMPLE_POINTS.brickExplosive` selecting the purple tough brick while
`SAMPLE_COLORS.brickExplosive` documents red. Candidate digest:
`122142436f1e1dcefc8f646536c2a7b2e54fba67f50c08d6307262f2cce94ad8`.
Seventeen retained browser checks do not override this source defect. Ticket #38
and Sprint 2 remain completed; Sprint 3 is planned. Existing release order also
asks QA for technical evidence before its production. Corrective planning must
address both the source defect and that dependency, preserving history.

The previously prepared SDK 5.10.2 / Producer 6.2.5 budget-only follow-up is not
sufficient recovery. `SpecialistAgent.GenerateIncidentOwnerReportAsync` in the
Producer overrides the shared SDK method and retains a 1,600-token, 60-second
report call and evidence-keyed fallback cache. Retarget unpublished release notes
to the final architectural release; preserve any versions already published.

## Implemented code and limits

- `WorkDeliveryService.EnsureCorrections` persists one manager-owned handoff per
  execution/candidate in `WorkDeliveryPlan.CorrectionsJson`, in the verdict and
  outbox transaction. `PulseAsync` backfills retained pre-upgrade rejections once.
- `CoordinateCorrectionAsync` binds actual same-project corrective work, preserves
  a specific planning wait, and resolves findings only after post-finding task QA
  and fresh follow-up acceptance. It pauses the old graph before a new writer
  starts, preserves its negative verdict, and supersedes its ticket only after
  acceptance. `ControlAsync` refuses to restart that rejected writer while linked
  corrective work remains. Revoked grants are not restored.
- `DeliveryCorrectionCoordinator.ReconcileProjectAsync` performs bounded current
  reads. Stable creation/finalization/activation keys recover lost replies. It
  creates one engineering task, source-preparation ticket and acceptance ticket,
  retains all original release criteria/steps, orders technical evidence before
  QA, and creates a separate corrective sprint after authoritative preflight.
- Automatic preparation currently covers one unambiguous completed code source
  and one repository with a direct mechanical Story-to-release binding. Multiple
  owners, custom integration topology, required planning approvals, unavailable
  workers and conflicting active sprints retain manager-owned planning actions.
  They do not select a random engineer, waive policy or start speculative work.
- The manager base invokes `HandleManagerRecoveryAsync` before incident reporting.
  Producer recovery uses current project identities and its existing portfolio
  discovery. `GenerateReportAsync` has no execution tools; `ProjectIncidentReview`
  preserves the execution assessment/deadline. The Producer's duplicate budget
  path and persistent fallback cache are removed.
- `ProjectHealthService.CurrentIncidentStateAsync` includes current corrective
  receipts in report freshness. Delivery UI shows the owner, wait and follow-up
  plan and hides conflicting retry controls. Assigned handoffs do not claim the
  linked sprint is running.

## Verification and rollout record

Prepared versions: Work Management Contracts **5.3.0**, SDK **5.11.0**, Producer
**6.3.0**. The previous unpublished budget-only notes were retargeted. QA remains
5.5.3 and Technical Director remains 6.0.5. No developer execution core changed.

Verification: 50 contract tests, 726 SDK tests, two sample tests, seven generated
agent-template tests and self-test, 505 package-only Producer tests, 263 platform
regressions (three existing skipped tests), and 217 package-only platform tests
(three existing skipped tests). Focused corrective tests additionally exercise
rollback and concurrent revisions against a fresh temporary PostgreSQL database.
Producer self-test, source-release guard, NuGet metadata and Linux bundle checks
pass. Its bundled SDK matches the packaged SDK exactly. Local verification logs
and package hashes are recorded in
`.artifacts/cool-games-review/corrective-architecture-verification.json`.
Producer bundle SHA-256: `a10889ad98e77f8a17c1075fdf17021e1cc05de22c85cc386e8968816826aa8f`.
The six final focused correction/rollback/concurrency tests pass after the last
writer-restart guard; none of these six were skipped.

Failure coverage includes a persisted task with a lost response, fresh-context
resume, failed sprint preflight, ambiguous ownership, missing engineer/revoked
capability, wrong manager/stale revision, report-provider failure after recovery
assignment, rejected-writer restart refusal, transactional failure before commit,
concurrent correction updates, and full corrected-source/QA/fresh-acceptance flow.
The PostgreSQL health regression's stale blanket-empty assertions were corrected
so informational status cards remain visible without fabricated owner decisions.

Rollout order:

1. Publish Work Management Contracts 5.3.0, then SDK 5.11.0.
2. Publish/install Producer 6.3.0 through Installed Agents, including its reviewed
   correction capability. Existing project enrollment reconciles missing manager
   action grants during authorized board reads; explicit revocations stay revoked.
3. Start/restart C-Sweet with the matching platform build. Its normal migration
   adds non-null JSON `CorrectionsJson` with `[]` for existing plans. No compute
   provider update or new network permission is required.
4. Verify the existing #43 rejection receives exactly one owned correction, that
   the actual engineer claims the new task, and that the separate sprint advances
   through independent QA and follow-up acceptance. Verify the rejected writer
   stays stopped, #38/Sprint 2 stay historical, and successor planning resumes only
   from accepted follow-up evidence. Do not retry merely to erase the finding.

Live rollout remains pending. C-Sweet's local containers/services were stopped at
preflight, so no live database state was changed and no Prism Break recovery is
claimed from these tests. The known retained source defect still requires actual
engineering correction after installation.

## Research basis

- [RoboCo QA lifecycle implementation](https://github.com/rennf93/roboco/blob/afc5fb79e83fb903a2d34262dc96c3a99890d9c9/roboco/services/gateway/choreographer/qa.py): persistent rejection, original-developer routing and notification-failure isolation.
- [RoboCo findings ledger](https://github.com/rennf93/roboco/blob/afc5fb79e83fb903a2d34262dc96c3a99890d9c9/docs/rag/architecture/review-findings.md): structured feedback and independent verification.
- [RoboCo progress policy](https://github.com/rennf93/roboco/blob/afc5fb79e83fb903a2d34262dc96c3a99890d9c9/roboco/foundation/policy/agent_loop.py): bounded repeated failures and unproductive respawns.
- [Claude Game Studio handoffs](https://github.com/Donchitos/Claude-Code-Game-Studios/blob/05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418/.claude/skills/dev-story/CONTRACT.md), [context](https://github.com/Donchitos/Claude-Code-Game-Studios/blob/05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418/.claude/docs/context-management.md), and [workflow tiers](https://github.com/Donchitos/Claude-Code-Game-Studios/blob/05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418/.claude/docs/workflow-modes.md): explicit contracts, bounded checkpoints and proportional planning. Adopt these ideas while preserving C-Sweet's stronger executed-evidence requirements.
