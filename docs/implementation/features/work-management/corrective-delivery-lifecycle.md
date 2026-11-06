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

Live platform rollout verified on 2026-11-05 (Pacific): the normal migrator applied
`20261106025444_CorrectiveDeliveryHandoffs` to the existing database and completed
setup seeding. API and AgentHost started. The delivery pulse created exactly one
Pending correction for #43's retained QA finding, owned by the existing Producer.
At the initial checkpoint Producer remained installed at 6.2.4. The subsequent
6.3.0 installation was verified with configuration `Current` and a running MCP
session. Its automatic wake processed the retained delivery event and changed
the same correction to `NeedsPlanning`, revision 2, without creating duplicates.
Actual engineering correction/independent acceptance remain pending.

The live recovery exposed a planning gap: the rejected plan has two completed
Code inputs in the same repository, #37 (dependency pin) and #38 (placeholder
scene). `DeliveryCorrectionCoordinator.ReconcileAsync` requires exactly one
source in SDK 5.11.0 unless `WorkDeliveryCorrection.SourceItemId` is already selected. It
therefore retained "Select the source task before corrective planning" and did
not create a repair task, follow-up plan or sprint. This is the tested conservative
ambiguity path; Producer 6.3.0 had no manager continuation selecting the source
for this live case. The reported scene defect belongs to #38's scope. The
multi-source follow-up below supplies a durable selection continuation while
preserving ambiguity handling for genuinely uncertain findings. Merely starting
the 6.3.0 runtime or retrying #43 does not resolve
this gap. Sprint 2 and #38 remain completed; Sprint 3 remains planned.

The first startup failed with EF's pending-model warning because the migrator's
copied `CSweet.Infrastructure.dll` predated the final migration. Rebuilding normal
startup outputs and restarting resolved it; no warning suppression, replacement
migration or manual database write was required. Stop the running AppHost before
rebuilding all its services to avoid GitHost/provisioner dependency file locks.
`MigratorModelParityTests.Real_startup_services_match_the_migration_snapshot_and_design_time_factory`
checks the real infrastructure registration against the snapshot and design-time
factory without opening a database connection. EF's `has-pending-model-changes`
check also passes against the rebuilt migrator. These checks verify source/model
parity; the live migration/startup verification additionally verifies deployed
build outputs.

## Multi-source manager continuation follow-up

Prepared versions: **SDK 5.12.0** and **Producer 6.3.1**. Work Management
Contracts remains 5.3.0. No additional platform migration, network access,
compute-provider, QA or Technical Director update is needed.

- `DeliveryCorrectionCoordinator.ReconcileProjectAsync` retains its existing
  overload and adds a lazy planning-client overload. Producer's
  `ReconcileDeliveryCorrectionAsync` uses its configured provider/model with
  invocation kind `delivery-correction-source` from both identity-based recovery
  and portfolio fallback. The client is created only for multiple source inputs.
- `DeliveryCorrectionCoordinator.SelectSourceAsync` in
  `DeliveryCorrectionCoordinator.Selection.cs` makes one tool-free planning call using at most eight
  completed in-plan source scopes and eight retained findings. Every finding must
  map to the same source, with distinctive verbatim accepted scope and finding
  evidence. Cross-source, missing, malformed, unsupported and genuinely uncertain
  choices retain a manager-owned wait. No task is guessed from author, ordering,
  repository identity or report prose.
- Before task creation, the coordinator rechecks the plan and all candidate task
  revisions/statuses, then saves `SourceItemId` and its rationale through
  `CoordinateDeliveryCorrectionAsync` with a stable correction key. A lost
  selection/task response or fresh invocation reuses that selection without
  another model call. Normal grants, original requirements, current staffing,
  separate-sprint preflight, independent QA and fresh acceptance remain enforced.

Verification: **748 SDK tests**, **two sample tests**, **506 package-only Producer
tests**, **eight platform correction/startup tests** (including real PostgreSQL
rollback/concurrency, no skips), and **seven generated-template tests** plus its
self-test. Producer's published-output self-test, source-release guard, NuGet
metadata, Linux bundle verification and exact bundled-SDK comparison pass.
The SDK coordinator tests include Prism Break's distinct scene/dependency
scopes, selection-before-execution ordering, cold restarts/lost responses,
stale selected or competing tasks, stale plans, denied selection, provider
failure/timeout, caller cancellation, invalid/missing/duplicate evidence and
findings spanning multiple sources. The Producer wake test exercises its actual
configured platform model and saves the selection before creating engineering
work, independently of incident reporting.

Rollout: publish SDK 5.12.0, then publish/install Producer 6.3.1 through Installed
Agents. The existing platform already supports the selection receipt; restarting
the whole application is not required by this follow-up. Verify the installed
Producer selects #38 from current evidence, binds exactly one corrective plan,
starts the separate corrective sprint, and the engineer claims the task. Keep
#38/Sprint 2 historical and retain #43's rejected verdict until a new candidate
passes independent QA and follow-up acceptance. These releases are prepared,
not installed or published by local testing; live engineering recovery remains
pending the Installed Agents update. Release hashes and logs are recorded in
`.artifacts/cool-games-review/multi-source-release-verification.json`.

## Research basis

- [RoboCo QA lifecycle implementation](https://github.com/rennf93/roboco/blob/afc5fb79e83fb903a2d34262dc96c3a99890d9c9/roboco/services/gateway/choreographer/qa.py): persistent rejection, original-developer routing and notification-failure isolation.
- [RoboCo findings ledger](https://github.com/rennf93/roboco/blob/afc5fb79e83fb903a2d34262dc96c3a99890d9c9/docs/rag/architecture/review-findings.md): structured feedback and independent verification.
- [RoboCo progress policy](https://github.com/rennf93/roboco/blob/afc5fb79e83fb903a2d34262dc96c3a99890d9c9/roboco/foundation/policy/agent_loop.py): bounded repeated failures and unproductive respawns.
- [Claude Game Studio handoffs](https://github.com/Donchitos/Claude-Code-Game-Studios/blob/05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418/.claude/skills/dev-story/CONTRACT.md), [context](https://github.com/Donchitos/Claude-Code-Game-Studios/blob/05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418/.claude/docs/context-management.md), and [workflow tiers](https://github.com/Donchitos/Claude-Code-Game-Studios/blob/05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418/.claude/docs/workflow-modes.md): explicit contracts, bounded checkpoints and proportional planning. Adopt these ideas while preserving C-Sweet's stronger executed-evidence requirements.
