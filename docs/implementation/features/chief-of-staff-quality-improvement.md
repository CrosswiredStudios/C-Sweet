# Chief of Staff quality improvement

Status: implementation in progress. Correctness fixes and the Phase 2/3 leadership candidate
are implemented in C-Sweet, Chief 2.14.0 and SDK 5.4.0, with persistence, broker and package tests.
Installed published-candidate/target-model/UI acceptance remains open. Broader Phase 4/5 work
remains behind G3; the focused dependency alignment needed by the leadership candidate is included.
See [implementation evidence and remaining work](chief-of-staff-quality-improvement-evidence.md).

Prepared: October 21, 2026.

## Objective and delivery order

Make the Chief reliably carry an executive decision through to an observable outcome.
Correct unintended effects and stalled work first, then complete the leadership-coverage
journey, then improve the wider implementation around behavior that has been verified.

The first complete journey is:

> The owner selects a focus. The Chief records it, checks current leadership coverage,
> identifies the smallest justified next action, tracks that action to an authoritative
> outcome, and advances to the next eligible assessment. Interruptions and retries do
> not duplicate recommendations, lose the decision, or change hiring authority.

This plan refines the sequencing in [Chief of Staff refinement](../../16-chief-of-staff-agent-refinement.md).
It preserves the existing [hiring authority](chief-hiring-autonomy.md),
[suggestion lifecycle](hiring-suggestion-lifecycle.md), and
[canonical work model](work-management/README.md).

| Phase | Deliverable | Dependency | Completion gate |
|---|---|---|---|
| 0 | Reproducible baseline and regression fixtures | None | G0: failures and package inputs are reproducible |
| 1 | Correctness patch | Relevant Phase 0 fixtures | G1: unintended effects and queue stalls are closed |
| 2 | Durable focus and coverage state | G1 | G2: authenticated focus activates exactly one assessment |
| 3 | Complete leadership journey | G2 | G3: assessment through resolution and continuation works after restart |
| 4 | Remaining obligations, incremental recovery, and measured efficiency | G3 | G4: promised follow-ups are tracked and repeated work is bounded |
| 5 | Selective SDK adoption and workflow decomposition | G3; G4 for affected workflows | G5: behavior is preserved with production wiring and package tests |

**G3 is the required product checkpoint before the broader refactor.** A small extraction
needed to test a fix, or a minimal typed contract needed for authenticated focus capture,
can ship earlier. A general handler framework, generic projector, or bulk SDK migration
must not become a prerequisite for the correctness patch or leadership journey.

Each gate requires recorded evidence. Work after a gate is not implicitly approved for
deployment by completion of this planning document.

## Evidence and limits

The starting point is the supplied `chief-of-staff-production-review-2026-10-21.md`
and source review of Chief commit `1642bb8`, manifest version `2.13.5`.
Recheck the starting revisions before implementation; the repositories can move independently.

- Source inspection confirms the prose-triggered Product Manager recommendation, incorrect
  Game Producer duplicate guard, unwired leadership agenda, repeated context pushes, and
  automatic sibling-SDK selection. The withdraw-and-replace queue failure needs an executable
  reproduction before its remediation is accepted.
- The current Product Manager `Plan` capability calls a deterministic builder. Do not count
  it as an extra model call. Context updates can still repeat CEO review messages and downstream work.
- The platform enforces spending, delegation, and concurrent-hiring controls during hiring.
  Improving the Chief's organizational assessment does not move those controls into the agent.
- The previous verification compiled the Chief and test projects against pinned SDK/memory
  packages with sibling references disabled. The test runner stalled without results and
  was stopped. There is no passing-suite baseline or measured latency baseline yet.
- The original report's round-trip counts and delivery estimates are hypotheses. Establish
  measurements before choosing performance targets or calendar commitments.

## Invariants for every phase

1. Authenticated platform state establishes authority, identity, decisions, and lifecycle
   outcomes. Model output, memory, display names, and question text do not establish them.
2. Domain records are authoritative. Personal cards expose and schedule obligations;
   existing platform audit cards continue to record callbacks and inference. Ordinary
   greetings and status questions do not need new execution cards.
3. Persist a promised follow-up before confirming it. Acknowledging, forwarding, creating
   a card, or requesting a hire does not establish that the underlying outcome occurred.
4. Commit domain changes and their outbox records atomically. Treat events as wake hints;
   re-read current authorized state before acting. Use stable operation keys, revision checks,
   and bounded reconnect discovery for duplicate, missed, and out-of-order delivery.
5. Required effects precede final turn commit. Optional post-response work is a durable
   obligation with its own recovery, rather than an untracked continuation after commit.
6. Direct owner requests and decisions, active hiring, manager escalations, and urgent work
   take precedence over proactive coverage. Preserve interrupted work at safe boundaries.
7. Keep organizational approval distinct from hiring execution. Current owner policy,
   approval-bound delegation where applicable, platform finance controls, grants, and
   candidate/configuration validation remain authoritative at execution time.
8. Preserve existing completed hires, decisions, approved work, conversation history, and
   installation-scoped hiring policy during upgrades. Never infer new consent from old prose.

## Phase 0: establish a bounded, reproducible baseline

### Work packages

- **C00 — Record release inputs.** Capture Chief, platform, SDK, memory, and affected contract
  revisions; manifest/package versions; relevant grants; and the declared provider/model.
  Verify the actual sibling SDK path: this checkout uses `CSweet.Agent.Sdk`, while older
  contributor instructions mention `CSweetAgentSdk`. Do not bump or test a different checkout.
- **C01 — Diagnose the test-runner stall.** Run discovery and a small deterministic subset
  separately from the full suite, with process/test timeouts and retained diagnostics.
  Distinguish environment/runner failure from test failure. An aborted or empty run fails G0.
- **C02 — Add behavior reproductions.** Prioritize prose recommendation creation,
  withdraw-and-replace, duplicate event delivery, hiring-preference misrouting, repeated
  PM context delivery, and the missing focus-to-assessment transition. Exercise public
  event/capability entry points, not only private formatting helpers.
- **C03 — Capture a small journey baseline.** Record platform calls, model invocations,
  time to first visible activity/draft, final-turn latency, and owner-facing messages for
  an ordinary question, onboarding, replacement hiring, and attention recovery.
  Label deterministic cross-agent calls separately from inference.

### G0 acceptance

The selected tests execute and report results against the declared package configuration.
Each correctness defect has a failing test or a documented source trace plus a specific
integration test to add with the fix. Unrelated runner diagnosis must not prevent preparing
and reviewing a narrow correctness patch; release still requires relevant checks to run.

## Phase 1: stop unintended effects and unblock existing work

Primary repositories: Chief and C-Sweet. Touch SDK/public contracts only for a demonstrated
missing boundary, rather than upgrading the whole dependency stack in this patch.

### Work packages

- **C10 — Remove prose-created staffing effects.** Delete
  `EnsureDefaultProductManagerRecommendationAsync` and its call. Recommendations come from
  validated tools or explicit deterministic workflows. Enforce turn-level ask-or-mutate
  policy across every recommendation path, not just wrapped model tools. Repeated ordinary
  discussion of Product Managers must create no recommendation or hire.
- **C11 — Reconcile recommendation cards in both directions.** Fix
  `SyncHiringPersonalTodosAsync` to reconcile obsolete cards even when the pending backlog
  is empty. Correlate by recommendation identity and authorized installation/scope, not title.
  Withdrawal should settle the correlated execution card as cancelled through the platform's
  canonical mutation service in the withdrawal transaction, with activity and outbox hints.
  Chief recovery repairs older orphan cards. Fulfillment completes work; withdrawal records
  cancellation. Respect existing claims/revisions so late worker completion cannot resurrect
  a cancelled item. Activate the next eligible hiring item idempotently.
- **C12 — Correct intake and finalization ordering.** Remove the post-commit
  `response.Contains("hiring")` setup trigger. Begin setup from an identified onboarding or
  owner preference-change intent before final commit. Process authenticated answers before
  general preference-change routing. Questions about changing a hiring plan must not restart
  the preferences wizard. Preserve `CurrentMessageContent` as the trusted current-message
  source; quoted history must not trigger configuration changes.
- **C13 — Harden malformed input, cancellation, and reconciliation scope.** Validate required
  conversation/installation/message identifiers before side effects; propagate host cancellation.
  Replace random consult retry identity with a stable source/operation identity; reject calls
  lacking one when a durable effect needs correlation. Scope remove-delta reconciliation to
  the approved team/role/source lineage and owned recommendations. Do not withdraw the same
  role from another team or installation. Classify stale events, transient failures, and
  missing authority/data separately; persist actionable blocked work instead of retrying forever.
- **C14 — Cover subscribed events deliberately.** Inventory manifest subscriptions against
  SDK runtime dispatch and Chief handlers. For management status/resource needs, read the
  authorized stored report, deduplicate intake, and persist any follow-up before acknowledging
  it. Verify mention and reminder delivery end to end, including any platform-created cards.
  Handle the intended obligation or remove an unused subscription with an explicit compatibility
  rationale. A routing test must prove the intended effect, not just acknowledge delivery.
- **C15 — Stop unchanged PM update effects.** Deduplicate by recipient and meaningful brief
  content, excluding delivery timestamps and source event IDs. Include changes in business,
  finance, workforce, objectives, and workstreams; the existing business-only `ContextRevision`
  is insufficient. Use a stable content fingerprint or explicit input revision set. Persist
  delivery intent and successful acknowledgment separately so a crash does not lose a push.
  Ensure a repeated update produces no repeated CEO review/downstream effects. Add receiver-side
  idempotency in the Product Manager if required. Remove the unused extra plan request; the
  existing context-update path must have one explicit, tested owner for review and follow-up.

### G1 acceptance and release

- Product Manager prose produces zero staffing mutations, including after a question tool ran.
- Withdraw-and-replace, withdraw-last-item, fulfillment, repeated withdrawal, and concurrent
  withdrawal/worker completion leave no orphan active card or blocked successor.
- A hiring-plan question does not change hiring preferences. Valid preference answers advance
  once; malformed and stale input cause no effects. No required effect runs after turn commit.
- Every declared subscription has a demonstrated handler/runtime path or documented removal.
- Repeated identical PM context has no repeated user-facing effect; a finance-only or workforce-only
  change still propagates. Failed delivery retries without duplicating completed effects.
- The focused regressions and affected platform integration tests pass against pinned packages.

Publish this as an independently releasable correctness update. Do not wait for Phase 5.
Do not blanket-delete historical recommendations as a migration: identify suspected duplicates
for review and automatically settle only work proven obsolete by authoritative source state.

## Phase 2: persist focus and make one assessment executable

Primary repositories: C-Sweet and Chief; SDK for the smallest required typed capability
surface. The names below are **proposed domain concepts**, not existing APIs.

### Work packages

- **C20 — Add authoritative focus and coverage records.** Use platform-owned persistence with
  authorized reads, revision-checked commands, stable operation receipts, and outbox events.
  The agent owns assessment policy; the platform owns record integrity, access, and atomic
  transitions. Reuse existing storage/services when they satisfy these requirements.
- **C21 — Bind decision answers structurally.** Extend the existing executive-decision and
  authenticated turn-context path with decision identity and an authorized immutable answer
  read. Validate organization, conversation, target installation, owner, question purpose,
  source revision, and answered status. Carry a stable option/domain ID, not an English label.
  Treat `Decision:` text as legacy presentation only; it never supplies authorization.
- **C22 — Connect onboarding to the agenda.** After existing profile and hiring-policy setup,
  present two to four relevant focus options with one native decision. Selecting one records
  focus and activates the matching assessment once. Preserve custom focus text as data. For
  a clear explicit current owner priority, validate/map and persist it without asking again;
  clarify only materially ambiguous choices. Do not create a hire at focus capture.
- **C23 — Project the state onto canonical personal work.** Maintain one onboarding parent
  plan where supported, domain assessment cards, and links to domain records. Keep unselected
  work in Backlog. Commands atomically persist transition intent and required delivery work;
  replay repairs a partial card projection. Avoid duplicate execution cards for the same
  obligation and keep platform audit cards separate.
- **C24 — Define migration and mixed-version behavior.** Backfill unassessed records from
  existing agenda cards using stable domain mappings. Preserve legacy operating-state keys
  or explicitly migrate them; changing a key helper must not restart onboarding. Recover an
  old answer only from its authenticated decision record. An unanswered/uncorrelated legacy
  focus remains awaiting input. New execution requires capability/version support; incompatible
  combinations report a visible configuration block instead of falling through to prose effects.

### Minimum record model

| Concept | Required information |
|---|---|
| Focus decision | Organization and scope; domain or custom focus; human decision maker; source message/decision/turn; effective time; revision; precedence/source; superseded decision |
| Coverage assessment | Stable identity by organization, profile version, scope, and domain; current responsible Chief installation; applicability; assessment status; accountable owner; coverage model; evidence/input revisions; priority; dependencies/activation triggers; next action; review time; linked work and recommendation |
| Delivery/operation receipt | Stable command identity; expected/source revisions; pending effect; linked domain/work IDs; acknowledgment/outcome; classified error and retry/review eligibility |

Keep assessment status separate from execution-card status. Useful assessment results follow
the refinement spec: `Unassessed`, `NeedsCoverage`, `InterimCoverage`, `AdequatelyCovered`,
`AtRisk`, `Deferred`, and `NotApplicable`. Define allowed transitions before publishing
contracts. Awaiting a decision or a hire is an execution state linked to those facts.

### G2 acceptance

An authenticated selection survives process restart, creates one focus revision, and leaves
exactly one corresponding assessment eligible to execute. Duplicate, stale, cross-organization,
and wrong-installation answers cannot advance state. Existing adequate coverage and hiring
policy are preserved. Before a selection, no proactive assessment or hiring recommendation runs.

## Phase 3: complete the leadership journey

Primary repositories: Chief and C-Sweet. Start with deep scenario coverage for General and
Game Studio. Preserve all existing selectable profiles; enable their new proactive execution
only after profile-specific fixtures pass. Do not silently remove profiles or enable untested
automatic coverage work for existing companies.

### Work packages

- **C30 — Implement a bounded assessment handler.** Read current focus, role assignments,
  active workforce, pending recommendations, relevant business evidence, budget constraints,
  owner policy, and operating profile. Identify people by authenticated role/assignment data,
  preserving organization and caller checks. Assess coverage, authority, and capacity rather
  than matching job titles or treating every domain as a vacancy.
- **C31 — Validate a typed assessment result.** Constrain model interpretation to a schema
  containing evidence references, coverage model, rationale, missing information, proposed
  next action, and review trigger. Validate references and state transitions in code. Bound
  model/format-repair attempts and checkpoint accepted results. Missing or contradictory
  evidence yields a specific question or durable block; it never fabricates a gap or completion.
- **C32 — Choose the smallest justified coverage action.** Recognize existing CEO ownership,
  combined roles, agent managers, interim arrangements, external/fractional professionals,
  deferral, and inapplicability. A human-required need must not become an automatic agent hire.
  Organizational changes requiring consent remain proposals. Record budget uncertainty rather
  than inventing approval or treating a zero package price as zero operating cost.
- **C33 — Link a verified gap to one recommendation.** For the highest-priority eligible
  uncovered domain, create or reuse one source-linked recommendation with validated scope
  and headcount. Atomically bind the assessment and recommendation, or persist an operation
  intent whose retry resolves the same identity. Reuse the existing hiring-card flow. Keep
  one unresolved proactive recommendation per organization; explicit requests and approved
  plans retain their own authority and sequencing. Do not merge unrelated needs just because
  their role family matches.
- **C34 — Observe resolution and continue.** Read authoritative hire fulfillment, withdrawal,
  approved assignment, owner deferral, or invalidation. Reassess actual coverage after a hire;
  package installation alone does not prove that accountability/capacity is adequate. A failed
  hire or unavailable candidate remains waiting/blocked with a recovery trigger. Once the
  current obligation resolves, activate the next eligible assessment exactly once. Exhausted
  agendas wait for an explicit review time or relevant change rather than looping.
- **C35 — Support focus changes and preemption.** At safe boundaries, prioritize incoming
  owner decisions, manager needs, approvals, and active hiring over proactive work. Checkpoint
  and release execution capacity while awaiting external outcomes. Re-rank after explicit
  focus changes without silently withdrawing approved or running commitments. Explain effects
  and obtain normal authorization for changes that require it. Resume eligible paused work
  from persisted state without replaying completed actions.
- **C36 — Recover and migrate the complete journey.** Discover unresolved records in bounded,
  paginated reads with a persisted continuation position. Reconcile after restart, upgrade,
  missed events, profile/workforce changes, and Chief replacement. Revalidate the current
  responsible Chief before writes; historical records persist, but predecessor delegation does
  not transfer. Candidate/configuration/policy changes may wake waiting work. Never mark an
  unresolved obligation complete merely to advance the queue.

### Transition expectations

| Observed condition | Persisted action | Board/successor behavior |
|---|---|---|
| Focus accepted | Save focus and assessment activation intent | Matching assessment becomes Ready; others stay Backlog |
| Current coverage is adequate | Save supported owner/coverage/evidence | Complete assessment execution and activate next eligible assessment |
| Essential information is missing | Save correlated decision obligation | Wait without holding an execution lease; resume from authenticated answer |
| Verified gap supports a recommendation | Link one recommendation and recovery intent | Use existing hiring card; proactive successors wait |
| Hire fails or no suitable candidate exists | Save actionable exception and recovery trigger | Retain unresolved work; no false fulfillment or repeated owner notices |
| Hire/assignment succeeds | Re-read workforce and reassess | Complete only on supported coverage outcome, then advance |
| Owner defers or withdraws a proposed action | Record decision and source outcome | Set review/invalidation state; resolve obsolete card and re-rank |
| New focus conflicts with approved work | Save revised focus and identify affected commitments | Preserve commitments pending the appropriate decision |

### G3 acceptance: first complete product checkpoint

Run the scenarios below through real platform persistence, broker schemas, canonical cards,
and an installed published prerelease Chief candidate in a disposable business. This is the
candidate acceptance gate before promoting the release or enabling production rollout.
Exercise the normal UI
for the focus widget, replacement hiring, and visible waiting/resolution states. A fake model
is appropriate for deterministic crash tests; include target-model conversations separately.

1. General: select Product, verify existing CEO coverage, avoid an unnecessary hire, and
   advance to another eligible assessment.
2. General: establish a supported leadership gap, create one recommendation, fulfill it,
   verify coverage, and advance once. Repeat with deferral and withdrawal/replacement.
3. Game Studio: respect current creative ownership and manager-owned subordinate hiring;
   do not create a conflicting company-level Product Manager/Game Producer recommendation.
4. External/fractional coverage: expose the decision and track it without attempting an
   unauthorized Marketplace hire or declaring the external engagement complete.
5. Restart after every persistence/effect boundary, including recommendation creation before
   acknowledgment. Miss and reorder events. The resulting focus, cards, recommendation,
   fulfilled seats, and owner-facing messages remain consistent and nonduplicated.
6. Change focus during assessment and during an active hire. Direct input is serviced at
   a safe boundary; approved commitments retain their authority and history.

**Do not begin a broad shell/handler rewrite until these journeys have retained evidence.**

## Phase 4: close remaining obligations and bound recurring work

### Work packages

- **C40 — Track manager and project follow-through.** Persist PM escalations awaiting an
  owner answer, approved resource-change reconciliation, and project-assistance obligations
  before promising follow-up. Reuse [delegation with tracking](delegation-with-tracking.md)
  and current coordination primitives where they fit. Bind answers to requests, deliver them
  to the authorized requester, and retain delivery acknowledgment. Existing platform audit
  cards are not substitutes for unresolved reporting obligations.
- **C41 — Use supported task outcomes.** Replace the generic `BLOCKED:`/otherwise-completed
  protocol for supported personal tasks with typed outcomes and evidence. Model claims alone
  cannot establish task success. Unknown tasks remain visibly unsupported; waiting tasks have
  a reason, source dependency, and event/review trigger. Bound automatic correction/retry.
- **C42 — Make recovery incremental.** Replace replay of the first 100 approved plans on every
  attention review with revision/effect receipts and fair pagination. A reconciliation marker
  must distinguish projection done from hiring unresolved; it must not hide later fulfillment,
  policy revocation, candidate availability, or changed plans. Verify eventual discovery beyond
  100 historical records and isolate a bad item so it cannot starve the rest.
- **C43 — Reuse callback context safely.** Load independent context reads concurrently where
  supported, gather results before constructing the snapshot, and reuse them within the callback.
  Invalidate after mutations; re-read authority and revisions before effects. Never use a cached
  context snapshot as a grant. Move fact extraction off reply latency only with a persisted
  capture obligation and ordering that prevents stale facts from overwriting newer corrections.
- **C44 — Improve approval quality and observable cost.** Assess role purpose, existing coverage,
  duplication, scope, dependencies, and known budget implications before organizational approval.
  Surface unknowns for review. Retain platform execution-time finance/delegation enforcement;
  do not require a hypothetical CFO agent. Measure latency distributions, queue age, platform/model
  calls, recovery age, and duplicate side effects. Redact/bound diagnostics and record public
  usage/results rather than hidden reasoning or raw secrets.

### G4 acceptance

Every promised future obligation has a durable record, visible status, and supported resolution.
Unchanged context causes no PM push effects or completed-plan replay. Recovery eventually reaches
every unresolved item in a history larger than one page. A failure in one item does not block
unrelated work. Compare ordinary-turn and recovery measurements with C03 and set explicit
per-scenario budgets from those results; do not adopt the report's arbitrary eight-call ceiling.

## Phase 5: adopt shared primitives and refactor proven workflows

### Work packages

- **C50 — Align released dependencies.** Select a compatible published SDK/memory/platform set
  from actual API requirements and contract tests. Make sibling source use explicit and retain
  a dedicated sibling-integration check. Adopt existing typed communication, user-input,
  operating-state, and role APIs where compatible. Migrate stored keys/schema explicitly.
  An SDK major upgrade requires a compatibility review, not just a changed package pin.
- **C51 — Extract workflow services incrementally.** Begin with onboarding/leadership, hiring
  reconciliation, and product liaison. Keep a thin dispatch shell where useful, with actual DI
  registration and resolution tests. Use `IEnumerable<T>` or explicit collection registration
  for handler collections. Test dispatch with substituted dependencies through production
  registration. Remove unused abstractions, stale profile copies, and dead paths after use
  and behavior checks. Success is clear ownership and testability, not a line-count target.
- **C52 — Promote only demonstrated shared needs.** Prioritize authenticated decision reading,
  typed configuration proposals, and common communication/state adapters. Extract stream
  bridging or projection only when two real consumers establish compatible semantics and
  contract tests. Keep Chief-specific leadership policy outside the SDK. Avoid a bulk rollout
  of speculative generic helpers across all agents.
- **C53 — Simplify prompts and presentation.** Compose role, mode, and profile instructions from
  validated context. Remove English regex filters only after equivalent effect guards and
  conversation evaluations pass. Use structured metadata for routing and bounded classification
  for ambiguous free text. Introduce locale-aware strings/options without changing stable IDs.
  Move profile catalogs to versioned data only when it improves tested behavior or maintenance.

### G5 acceptance

G1-G4 journeys remain green through production DI and published-package builds. There is one
active source of prompt/identity/configuration truth, or tests explicitly enforce parity.
New SDK helpers have real consumers and preserve authority, state, and compatibility behavior.
No legacy state-key change restarts onboarding or loses obligations.

## Required regression and evaluation matrix

Add the relevant cases with each phase; evaluation is not deferred until the final refactor.

| Scenario family | Required assertions | First gate |
|---|---|---|
| Prose and tool policy | PM discussion has no effect; ask-or-mutate works in either order and across deterministic paths | G1 |
| Hiring replacement | Empty backlog, orphan active card, cancellation race, repeated withdrawal, fulfillment, next activation | G1 |
| Scope and authorization | Same role in another team/installation untouched; spoofed identity and stale decisions rejected | G1/G2 |
| Input/failure handling | Malformed IDs, cancellation, provider failure, transient retry, durable block, stale ignore | G1 |
| Event coverage | Actual subscription dispatch, current-state read, idempotent intake, mention/reminder behavior | G1 |
| PM context | Identical context no-op; finance/workforce-only change delivered; failure/retry does not repeat CEO effects | G1 |
| Focus | Known option, custom answer, explicit initial priority, duplicate answer, old question, legacy migration | G2 |
| Coverage | CEO/combined/agent/external/interim coverage; unsupported evidence rejected; no vacancy assumed | G3 |
| Lifecycle | Gap to recommendation to verified coverage; failure, deferral, replacement, next assessment | G3 |
| Recovery | Restart at each effect boundary; duplicate/out-of-order/missed event; predecessor Chief fenced | G3 |
| Scheduling | Owner/manager input preempts safely; waiting work holds no execution slot; eligible work resumes | G3 |
| Durable follow-up | Escalation answer relayed once; receipt retained; no completion from model acknowledgment | G4 |
| Scale | More than 100 plans; fair recovery pages; bad record isolation; no replay proportional to completed history | G4 |
| Conversation quality | Clear next action, concise one-question behavior, correct ownership, no repeated setup, multilingual input | Each affected gate |
| Compatibility | Pinned packages, production DI, capability absence, migration replay, fresh and upgraded installation | Every release |

For model evaluations, use versioned fixtures, configured target models, public output/tool traces,
and a recorded rubric. Zero unauthorized effects, duplicate hires, or false lifecycle completions
are release requirements. Establish wording/quality thresholds and latency budgets from the
baseline; record model variability separately from deterministic failures.

## Code map for implementation

Paths below are relative to the named repository; existing symbols are starting points.
New public types and capability names must follow the owning repository's conventions.

| Repository | Existing files/symbols | Planned responsibility |
|---|---|---|
| `CSweet.Agent.ChiefOfStaff` | `Agent/ChiefOfStaffAgent.cs`: `HandleManagerEventAsync`, `HandlePersonalTodoAsync`, `SyncHiringPersonalTodosAsync`, `EnsureLeadershipCoverageAgendaAsync`, `PushProductManagerContextUpdatesAsync` | Correct effects, connect agenda execution, eventually extract workflows |
| Chief | `Agent/ChiefOfStaffAgent.HiringPolicy.cs`, `.OperatingProfile.cs`, `.Projects.cs` | Intake ordering, legacy onboarding recovery, resource/project follow-through |
| Chief | `Orchestration/ChiefOfStaffOrchestrator.cs`, `Profiles/BusinessOperatingProfiles.cs`, `Policies/HiringTurnTools.cs` | Meaningful context inputs, coverage policy, tool effect guards |
| Chief | `Hosting/Program.cs`, `Directory.Build.props`, project files, `csweet-plugin.json`, tests | Production wiring, published dependency checks, release identity |
| `csweet` | `Core/HiringService.cs`, `.Autonomy.cs`, `ResourceChangeService.cs` under Infrastructure | Withdrawal/card transaction, scoped reconciliation, existing execution authority |
| C-Sweet | `Communications/ExecutiveDecisionService.cs`, `ChatTurnWorker`, `MemoryRecallDispatchEvidence` | Immutable answer read and authenticated turn correlation |
| C-Sweet | `WorkManagement/PersonalTodoService.cs`, `ChiefWorkCards.cs`, persistence/outbox code | Canonical card projection, source lifecycle settlement, durable delivery |
| `CSweet.Agent.Sdk` | `CSweetManagerAgentBase`, `AgentRuntimeWorker`, `AgentTestRuntime`, typed platform clients | Verify dispatch; minimal new typed boundaries; later shared implementation |
| `CSweet.Agent.SoftwareProductManager` | `ProductManagerAgent.ExecuteCapabilityCoreAsync`, `RouteContextUpdatePlanToCeoAsync`, `ProductManagerOrchestrator.BuildContextUpdateResponse` | Receiver idempotency and explicit context-update effects if C15 requires changes |

## Release and rollout requirements

1. Build each release from recorded source/package inputs. The default shipping verification
   disables sibling project references; run sibling-source integration separately. At baseline,
   the Chief package check uses `UseLocalCSweetAgentSdk=false` and `UseLocalCSweetMemory=false`.
   Discover the corresponding switches for each other consumer rather than assuming parity.
2. For every changed agent, bump/synchronize manifest, implementation identity, project/package,
   and assertions first. Read the final manifest version, then write `releases/<version>.md`
   with that version in its heading. Preserve published historical notes.
3. Version any changed SDK or shared contract package according to its public API change,
   synchronize consumers/documented pins, build/test/pack, and inspect the resulting `.nupkg`
   version. Apply the contributor rules for WorkManagement/Office contracts if touched.
4. Phase 1 should remain agent/platform compatible where possible. For new records/capabilities,
   deploy additive platform schema and broker support before the requiring agent. Record the
   tested compatibility matrix and behavior when capability or grant support is absent.
5. Run fresh-install and upgrade journeys in a disposable business with published packages.
   Install agent changes through the Installed Agents update flow. Never patch stored approved
   manifests, digests, package records, or installed runtime payloads to apply source fixes.
6. Enable new proactive leadership execution deliberately after G3, with a runtime control that
   pauses new proactive work while preserving state and existing hiring obligations. Revalidate
   that control at execution boundaries. Keep the rollout cohort bounded and inspect correctness,
   duplicate effects, blocked-work age, and owner-message volume before expanding it.
7. Roll back by pausing the new proactive path or installing a compatible published agent.
   Prefer additive schemas; preserve completed outcomes and unresolved records. If an older
   agent cannot interpret new state, block that downgrade or keep new obligations safely paused
   with a visible reason. Do not claim a successful rollback merely because the process starts.

## Decisions to review before implementation

The defaults below make the plan executable without an architectural redesign. They are
proposals for review, not descriptions of implemented behavior.

| Decision | Recommended default | Why |
|---|---|---|
| Authoritative leadership state | Platform-owned focus/coverage records; agent-owned assessment policy | Atomic links, cross-installation continuity, authorized discovery, visible recovery |
| First deep validation profiles | General and Game Studio; preserve other current profiles with proactive execution gated | Covers the primary ownership rules without removing existing configuration |
| Proactive pacing | One unresolved proactive recommendation per organization | Avoids competing Chiefs/conversations flooding the owner; explicit plans remain distinct |
| PM refresh ownership | Existing context-update workflow owns review/follow-through; remove the Chief's unused extra request | One supported consumer and fewer repeated effects |
| SDK sequencing | Minimal required typed contracts early; broader adoption/extraction after G3 | Correctness and complete behavior arrive before abstraction work |
| Legacy duplicates | Identify/report; automatically repair only proven stale source-linked cards | Preserves intentional headcount and avoids cancelling valid commitments |
| Timeline | Estimate work packages after G0 and schema/compatibility review | The existing report provides no measured basis for calendar promises |

## Implementation handoff evidence

For each phase, retain changed repositories/versions, executed checks and results, migration and
replay evidence, sample domain-to-card links, relevant public activity traces, and remaining
limitations. Mark a gate complete only when its stated journeys execute successfully. A passing
unit suite, attractive transcript, completed audit card, or successful local build alone does
not prove a completed leadership workflow.
