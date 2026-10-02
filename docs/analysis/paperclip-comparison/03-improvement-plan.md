# Prioritized C-Sweet improvement plan

[Assessment overview](README.md) · [Product comparison](01-product-and-workflows.md) · [Architecture](02-architecture-and-reliability.md) · [Evidence](04-evidence-and-reuse.md)

This is a proposed backlog, not an implementation commitment. Priorities favor reliable accepted work and low human coordination cost. Effort is an initial planning judgment: **S** means a few focused engineering days, **M** roughly one to two engineering weeks, and **L** several weeks or multiple repositories. Estimates exclude unknown migration, environment and provider qualification work; re-estimate after a short design review.

## Recommended order

| ID | Improvement | Priority | Effort | Dependencies | Main benefit |
|---|---|---|---|---|---|
| Q01 | Enforce core regression and package-boundary CI | P0 | M | Existing tests/build switches | Prevents avoidable regressions while improving the product. |
| Q02 | One reproducible delivery and recovery scenario | P0 | M–L | Q01; usable Office fixture | Establishes whether the company workflow really works. |
| Q03 | Unified human action queue | P1 | M | Existing approval/activity APIs; project-health integration when ready | Reduces the effort required to find and resolve blocked work. |
| Q04 | Paid inference admission and settlement | P1 | L | Accounting contract and concurrency tests | Connects autonomy to enforceable financial policy. |
| Q05 | Readiness report and first-work diagnostic | P1 | M | Existing setup and Office readiness | Shortens setup and troubleshooting. |
| Q06 | Completion evidence contract and review view | P1 | M | Canonical artifacts, orchestration and source control | Makes “done” inspectable and revision-specific. |
| Q07 | One portable business/team template | P2 | L | Q02, Q05; stable package pins | Makes successful configurations repeatable. |
| Q08 | Governed routine authoring | P2 | M–L | Scheduling policy, Q04, Q06 | Makes recurring work visible, bounded and reviewable. |
| Q09 | Approved playbooks with evaluation and rollback | P2 | L | Existing reliability plan criteria/contract phases | Improves procedures without changing authority. |
| Q10 | One external runtime bridge feasibility slice | P2 discovery | L | Office design review; Q02 and Q04 | Tests broader ecosystem access without weakening isolation. |
| Q11 | Visual and accessible interaction regression | P1 foundation, ongoing | M | Q01 and fixture data | Protects dense operator and communications workflows. |
| Q12 | Company backup and restore rehearsal | P1 design, P2 delivery | M–L | Storage and identity inventory | Establishes recoverability beyond a database backup. |

P0 means foundational work that should begin first. P1 means the next product-quality tranche. P2 means valuable work after the foundations, not “unimportant.” Q03 and Q05 can deliver incremental value while Q04 is designed. Avoid implementing all twelve simultaneously.

## Q01 Core regression and package boundaries

**Borrow from Paperclip:** ordinary PR validation, release verification and a separation between fast and environment-dependent tests. [P11](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** extend the main repository's Actions coverage beyond `windows-build.yml`. Use the existing unit/integration suites, release scripts and actual `UseLocal*` switches from `Directory.Build.props`. Keep sibling package builds separately owned; add a consumer compatibility lane.

**Acceptance:** a normal source PR triggers build and relevant deterministic tests; an isolated PostgreSQL lane exercises real locking/transactions; one lane restores and builds without sibling references; release packaging verifies expected package versions. Changes to contracts follow the existing version/pin/pack rules. An intentionally failing core regression must prevent the relevant merge gate from passing.

**Risk:** broad suites may contain existing failures or environment assumptions. Inventory and fix or explicitly isolate them with owners and expiry, rather than making the gate permanently optional. This assessment did not run or classify the full test suite.

## Q02 Reproducible delivery and recovery

**Borrow:** runner conformance, fault injection, release smoke and scenario-based evaluation. Reuse the ideas rather than starting with Paperclip's full harness. [P03, P11](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** extend `docs/implementation/features/game-production-reliability/02-phase-1-golden-path-evaluations.md` and the existing benchmark infrastructure. Start with deterministic agents/tools before live model scoring. Use one software fixture first if it makes validation cheaper; then prove the game vertical slice.

**Acceptance:** from a disposable organization, provision a minimal approved team, create one canonical work item, dispatch through the real Office boundary, publish an artifact, request a revision, accept the revised evidence, and record consumption. Inject a process restart, duplicate wake, stale completion and lost response. Final assertions require one accepted deliverable and no duplicate business mutation.

**Measure:** setup completion, time to first accepted output, human interventions, retries, unresolved blockers, consumption coverage and independent artifact checks. Keep deterministic pass/fail separate from optional human/model quality ratings. Do not rank models using missing-usage runs as if their cost were zero.

## Q03 Unified human action queue

**Borrow:** Paperclip's attention source taxonomy, resolver context, grouping, snooze, history and keyboard workflow. [P05](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** expand projections around `ApprovalDashboardService`, `IApprovalDashboardService`, `Approvals.razor`, `CommandCenter.razor`, Current Activity, notifications and Communications deep links. Add project incidents only through their canonical service and current authorization. Do not create another approval or incident table merely to render a queue.

**Suggested item fields:** canonical source/type, affected project/work item, concise reason, observed evidence time, severity, authorized resolver, proposed next action, decision revision, and current status. These are proposed fields, not an existing contract.

**Acceptance:** an authorized human can resolve a hire, review a deliverable, answer an agent question, inspect a failed run and handle a budget incident from one queue. Stale cards cannot mutate newer state. Snoozing affects the user's presentation rather than silently pausing work or granting authority. Reconnect converges from authorized state. Restricted chat content is not leaked into organization-wide items.

**Measure:** time from an actionable block to the right human decision, and number of screens needed. Establish a baseline before setting an improvement target.

## Q04 Paid inference admission and settlement

**Borrow:** explicit budget scopes, warning/hard incidents, pauses, resumptions and invocation checks. Go beyond observed-spend stops where C-Sweet promises a hard bound. [P08](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** build around existing `Budget`/`BudgetReservation`, `EvaluateBudgetAsync`, `AgentRunLog`, inference attribution and both streaming/non-streaming provider paths. Include memory enrichment and background model work in the path inventory. The actual enforcement boundary must precede provider dispatch; UI estimates are advisory.

**Proposed lifecycle:** estimate/reserve → authorize dispatch → record provider attempt → settle actual usage → release unused allowance. Reserve and admission must be durable and serialized across applicable scopes. Preserve estimates, provider-reported amounts, billing category and unknown coverage separately. For providers without bounded/reportable costs, expose a defined policy instead of asserting a hard currency guarantee.

**Acceptance:** concurrent requests cannot each consume the same remaining allowance; organization and narrower scope budgets reconcile; retries use distinct attributable attempts without double settlement; a lost receipt remains unknown/reserved until reconciled; revocation prevents queued dispatch; increasing a budget requires current authority; operator pause does not disappear when a budget incident is resolved.

**Important implementation audit:** test the current read-then-reserve method and most-restrictive-budget accounting before extending it. This plan identifies verification questions, not a confirmed production financial defect. State the residual exposure from in-flight usage and provider cancellation explicitly in the product's budget semantics.

## Q05 Readiness and first-work diagnostics

**Borrow:** the diagnostic CLI pattern and a simple first-use path. [P01](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** compose existing setup checks, provider tests, import/grant validation, Office health/certification, execution fleet progress and source-control readiness. The assisted installer already exists; improve its integration. Provide a UI result and optionally a CLI command backed by the same typed diagnostic service.

**Acceptance:** one report distinguishes missing credentials, unsupported model behavior, stale grants/package, offline Office, expired certification, storage failure and missing source-control access. Each result names the affected component, evidence time and safe next step. A bounded disposable agent probe demonstrates dispatch/result return. It performs no external writes or billable inference without the configured probe policy. Reports redact secrets and do not require copying raw logs into chat.

**Measure:** first-use completion and time spent on each prerequisite. Avoid an unmeasured “five-minute setup” promise. Preserve certification and enrollment checks even when packaging gets simpler.

## Q06 Completion evidence and review

**Borrow:** runtime-enforced completion/review and visible work products. Paperclip's required-comment backstop helps prevent silent results, but a comment alone is not proof of quality. [P04, P10](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** `WorkOrchestrator`, `Artifact`/`ArtifactRevision`, delivery review, source-control changes and the planned stable acceptance criteria/remediation ledger. Keep one authoritative work item and artifact model.

**Acceptance:** completion shows the exact revision/commit, acceptance criteria, test or preview evidence, unresolved findings, reviewer and provenance. Changing the candidate invalidates any revision-specific acceptance as required by policy. A failed or cancelled execution cannot appear accepted because it produced a final chat message. Repeated submission is idempotent. The user can distinguish output submitted, review passed and publication/merge completed.

**Risk:** avoid a universal evidence schema so elaborate that simple writing tasks need software-test fields. Use a small common envelope with task-type-specific evidence requirements.

## Q07 Portable business template

**Borrow:** company import/export preview, collision handling, dependency declaration and portable identity mapping. [P06](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** existing operating profiles, agent definitions, manifest grants, hiring workflows, workstream profiles, artifacts and benchmark blueprints. Begin with **one small software team template**, or one minimal game-prototype team if its acceptance fixture is already stronger.

**Acceptance:** export records schema version, source/version digests, logical identities and prerequisites. Preview lists new resources, collisions, unavailable packages, grants to approve and expected model/tool needs. Import creates dormant resources through normal services. No secrets, Office enrollment, active tokens, live grants or private conversations are copied. A repeated import key cannot duplicate a team, and altered payload under the same key fails. Round-trip tests verify meaning, not byte-identical database IDs.

**Risk:** a template is executable organizational configuration. Approval must be scoped to its concrete version. Do not automatically hire or spend just because an imported file requests it.

## Q08 Governed routines

**Borrow:** routines as inspectable resources with revision, timezone, trigger policy, concurrency, catch-up and execution history. [P07](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** reuse platform-owned schedules, durable inbox/outbox delivery and canonical work items. A routine may create a task or proposal; it should not execute an unrelated private workflow engine.

**Acceptance:** the operator can configure a weekly report with owner, scope, budget and expected artifact. Duplicate triggers coalesce according to policy. Offline recovery follows a documented skip/catch-up choice; daylight-saving transitions are deterministic. Paused/unauthorized routines do not dispatch. Every occurrence links to its work and result. There are no agent polling loops to discover due work.

## Q09 Playbook evaluation and promotion

**Borrow:** Skill Studio's practical author/test/use loop. [P09](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** follow the existing curated-playbooks phase, using procedural memory, evidence references, canonical document revisions and authorized promotion. Do not create another vector store or treat a repeated model assertion as verified learning.

**Acceptance:** draft a procedure from a verified finding, evaluate against saved fixtures, approve a version for a role/project, record the version supplied to a run, compare outcomes and roll back. Draft or superseded procedures cannot quietly enter high-trust assignment context. No procedure can broaden tool grants. Approval, publication and recall authorization remain separate checks.

## Q10 External runtime bridge experiment

**Borrow:** adapter contracts for diagnostics, resume metadata, normalized errors, usage basis and cancellation. [P02, P03](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** a dedicated agent/plugin bridge that runs only within an approved Office-compatible execution design. Select one runtime based on an actual user workflow. The feasibility question is whether authentication, filesystem work, model access and tools can remain compatible with broker policy.

**Acceptance:** demonstrate installation, bounded assignment, approved tool access, cancellation, restart/resume, usage attribution and credential containment. Reject the experiment if it requires arbitrary host processes, unmanaged network access or importing provider secrets into the wrong trust boundary. An external runtime may need adaptation; “supports an OpenAI-compatible endpoint” does not prove it can satisfy this contract.

**Decision gate:** compare engineering/qualification cost to adoption benefit after one working slice. Do not commit to twelve adapters because Paperclip has twelve directories.

## Q11 Interaction regression

**Borrow:** component fixtures, visual baselines and browser smoke. [P11](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** retain Blazor/MudBlazor and the existing design tokens. Add deterministic browser fixtures around Communications scrolling, action queue, review evidence and setup errors. Use representative narrow/wide viewports, keyboard navigation and accessible-name checks.

**Acceptance:** key workflows work without a pointer; focus survives queue updates; long messages and artifacts remain scrollable; the approval dialog names its decision and current revision; loading/empty/error states have clear actions. Visual baselines must be reviewed, not automatically accepted after every mismatch.

## Q12 Restore a company after failure

**Borrow:** operational checks and release/restore thinking, not a claim that Paperclip portability is a complete backup. [P01, P06](04-evidence-and-reuse.md#source-register)

**C-Sweet fit:** inventory PostgreSQL, artifact/repository/LFS storage, protected configuration and assignment trust, package artifacts and Office identities. Internal Git already has backup services, so extend the recovery story around them rather than duplicating them.

**Acceptance:** restore a disposable deployment, recover documents and repositories, identify stale leases, reconnect or reenroll Office safely, and reconcile work without duplicate effects. Record the restored point and any unrecoverable in-flight usage. Backup credentials/keys follow their own protected recovery process. Define recovery objectives only after measuring the drill.

## What to postpone or avoid

- A wholesale React/TypeScript rewrite: no inspected evidence makes framework replacement the best quality investment.
- Another canonical task, approval, memory or document subsystem: C-Sweet already owns these responsibilities.
- Automatically retrying uncertain external effects: positive recovery evidence and current authority must govern replay.
- Removing Office protections to match local setup convenience: improve packaging and diagnosis instead.
- More specialist roles before the minimal delivery scenario passes: breadth increases coordination costs.
- Importing all Paperclip code or dependencies: incompatible runtime and persistence assumptions would dominate the port.
- A marketplace expansion before repeatable installation and delivery: agent discovery is already available and is not the principal missing quality loop.

## Suggested delivery checkpoints

Use outcome gates rather than dates until staffing and baseline failure rates are known:

1. **Reliable baseline:** Q01 plus a first deterministic Q02 scenario and Q11 fixtures.
2. **Operable company:** Q03, Q05 and Q06, with existing project-health work integrated and verified.
3. **Bounded autonomy:** Q04 and a restore design/drill from Q12; repeat recovery scenarios.
4. **Repeatable expansion:** Q07, then Q08/Q09; decide whether Q10's bridge justifies further investment.

The strongest demonstration is a saved, independently verifiable business outcome with known interventions and consumption. That should be the acceptance standard for borrowing from Paperclip.
