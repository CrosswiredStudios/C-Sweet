# What C-Sweet should learn from RoboCo

Prepared 2026-09-07. Scope: software delivery, agent authoring, operational reliability, and security. This is an analysis and proposed backlog, not an implementation or security certification.

**Recommendation:** preserve C-Sweet's execution and authority boundaries, and invest first in proving complete delivery, enforcing requirement coverage, and making the agent's next valid action explicit. RoboCo's strongest contribution is the operational detail around handoffs, rejection, review, and recovery. Its fixed company structure and container/credential model are poor fits for C-Sweet.

## Architectural constraint: broad business applicability first

**Owner direction:** add functionality to C-Sweet core only when it is broadly applicable to the majority of businesses the platform serves. A high-impact improvement for a software company does not automatically belong in core or the base Agent SDK. The priority ranking below is an ecosystem investment order, not a list of core features to build.

This follows the existing [domain-neutral platform and publisher-owned extensions](../implementation/domain-neutral-extensions.md) boundary: core owns assignments, revisions, evidence, grants and bounded profile interpretation; installed agents and profiles own domain terminology and decisions. Existing software-specific core code is a compatibility constraint, not a precedent for adding more. Extraction can be separate work; this analysis does not require a disruptive rewrite.

### Admission rule for core and the base SDK

For each proposed addition, identify the recurring business need, why existing generic services cannot satisfy it, and the smallest shared invariant that requires platform ownership. Demonstrate the same semantics in materially different businesses, such as an agency delivering a campaign, a service company fulfilling a client request, and an operations team completing procurement. Several examples are a design check, not proof of majority applicability. When broad demand remains uncertain, implement the feature in an extension and revisit promotion after adoption evidence.

Generic naming alone does not qualify a feature for core. New schema, UI, background jobs, dependencies and migration costs must be justified for the shared platform. Businesses that do not install an extension should not acquire its workflow stages, domain tables, vocabulary, navigation or mandatory services. Core and base SDK must build and operate without it.

- **Core:** minimal reusable authority and coordination mechanisms: identity, grants, approvals, durable work, revisions/evidence, generic policy gates, resource limits and recovery.
- **Base Agent SDK:** transport-neutral clients and authoring/testing conveniences for those shared mechanisms. Optional SDK libraries own software, game or other domain-specific contracts and helpers.
- **Agents, plugins, toolchains and workflow profiles:** domain schemas, rubrics, role arrangements, implementation logic, integrations, procedures and domain UI contributions. Use existing bounded profile metadata where it suffices.
- **Development/test tooling:** evaluation runners and fixtures. A valuable benchmark does not need to become a production feature or mandatory dependency.

Extension ownership does not mean extensions become trusted. Core still authenticates decisions, validates declared structures and revisions, checks grants, and enforces approved generic transitions. Extension-supplied executable validation runs through the existing isolated execution boundary; never load arbitrary plugin code into a privileged service to make a gate extensible. Domain review outcomes are attributed evidence, not permission to bypass platform policy.

### Placement of every recommendation

| Rank | Small shared platform/SDK contribution, if needed | Functionality outside core |
|---|---|---|
| 1 | Reusable test adapters for public services; no new production workflow engine | Evaluation runner and fixture packages; software-company benchmark in the software-delivery test suite |
| 2 | Existing generic evidence/review primitives first; promote criterion identity, coverage and issue transitions only after the admission rule is met | Software QA validator fix, criterion interpretation, defect taxonomy, severity rubric and required development review stages |
| 3 | Projection of pinned assignment, live authority and approved generic completion requirements | Source/build/PR fields, domain prompt fragments and workflow-specific rendering through versioned profile payloads |
| 4 | Structured failure contract, effect status and authorized action hints | Domain-specific rejection reasons and remediation handlers |
| 5 | Generic resource reservations, usage attribution, deadlines and bounded retry/progress accounting | Token pricing adapters, domain cost estimates and specialist progress signals |
| 6 | Durable effect intent, idempotency, evidence-revision binding and reconciliation protocol | Git/PR/merge behavior, assembled-code testing, and other integration-specific effect adapters |
| 7 | Generic prerequisite aggregation over installed capabilities | Model/toolchain/repository/domain probes supplied by their owning components; no software prerequisites for other businesses |
| 8 | Existing scoped memory, provenance and approval APIs; no default new playbook subsystem | Procedure authoring, curation rubrics, applicability logic and optional playbook experience |
| 9 | Existing scheduling, grants, budgets and deduplication; add a generic primitive only if missing | Program catalogs, triggers interpreting business metrics, diagnostics and proposal generation; no built-in board-program suite |

Core acceptance tests should demonstrate shared semantics with at least one non-software fixture, and operation with all domain extensions absent. Domain tests should prove software-specific success through installed extensions. Optionality does not weaken security checks on installed capabilities.

## Evidence and limits

RoboCo was inspected at [`fc01630ef36413d79c4296ed8243e7940e19e676`](https://github.com/rennf93/roboco/tree/fc01630ef36413d79c4296ed8243e7940e19e676), dated September 4, 2026. The review covered actual implementation, lifecycle policy, runtime configuration, evaluation harness, and selected tests. Its README describes an experimental company of 25 agents and a human CEO, and explicitly identifies the software as an early prototype. That establishes the intended product, not autonomous-company performance. [R1]

C-Sweet was inspected from current local working trees, including existing uncommitted changes. Main repository HEAD was `9169e83ca56afbab629bd1caf4305a7679f3b9ac`; SDK HEAD was `21f6ad9c716b8406fbb06cf1d633bd31d6c2a9be`; WorkManagement.Contracts HEAD was `a339b76982075f188a7db5d08e955e2f3ced239b`; Office HEAD was `762d5f4dfcfd530bc1df8d75cee9a33b60a72507`. These commits alone do **not** reproduce modified local files. The companion [source snapshot](roboco-analysis-source-snapshot.json) records SHA-256 hashes of the principal evidence files.

The active SDK checkout is `CSweet.Agent.Sdk`, while contributor instructions still name the older `CSweetAgentSdk` path. The older directory contained no source files during inspection. Future package work must resolve that discrepancy before applying versioning instructions. No package source or version was changed for this analysis.

**Confidence labels:** “observed” means inspected implementation; “planned” means a design document, not delivered behavior; “inferred gap” means the inspected paths do not establish the capability. Tests were read, not executed. Neither company was run through a live task, penetration-tested, or benchmarked here. No comparative productivity, cost, or production-security score is claimed.

## What already exists in C-Sweet

| Capability | Observed C-Sweet baseline | Decision |
|---|---|---|
| Governed workflow | Pinned policy revisions, stage assignments, retry and transition traversal limits | Extend existing orchestration; do not add a second state machine. [C1] |
| Review and merge | Exact-current-SHA QA evidence, signed/expiring lead authorization, current repository policy, optional administrator approval, durable merge jobs | Preserve this foundation. Role labels and “QA passed” messages cannot replace it. [C2] |
| Durable execution | Idempotency/content binding, installation queues, leases, deadlines, bounded attempts and progress | Improve recovery around existing delivery rather than creating another queue. [C3] |
| SDK | Typed clients, live model-tool grants, role profiles, operating state, collaboration artifacts, in-memory testing | Add authoring conveniences while keeping transport private. [C4] |
| QA evidence | Named criterion results, validation commands/exit codes, findings, immutable artifact decisions, source revisions | The gap is durable identity and authoritative coverage, not absence of structured QA. [C5] |
| Models and spending | Provider concurrency/queue controls, inference wait accounting, token telemetry, separate financial budgets/reservations | Verify end-to-end work-cost enforcement; budgets and quotas are not entirely missing. [C6] |
| Memory | Scoped integration, procedural memories, trust/confirmation metadata, recall-use records | Build curated learning on this substrate, not a second vector store. [C7] |
| Isolation | Signed Office assignments, privileged revalidation/replay protection, certified provider selection, Hyper-V network-adapter removal | Preserve and test these boundaries while addressing broker misuse. [C8] |

There is already a [game-production reliability initiative](../implementation/features/game-production-reliability/README.md) covering evaluations, criteria/findings, effective contracts, playbooks/programs, and readiness. Its [delivery checklist](../implementation/features/game-production-reliability/07-junior-developer-checklist.md) is unchecked. Inspected contracts still use string acceptance criteria and per-result findings. Treat this as an existing plan to extend with software-delivery fixtures, not proof that its phases shipped. This analysis adds current code evidence, SDK deltas, failure cases, and a broader priority order.

## Improvements ranked by expected impact

Ranking favors trustworthy completed work and reduced operator intervention before feature breadth. Impact and effort are qualitative judgments, not measured gains. Small means a bounded component change; medium crosses services; large requires a cross-repository rollout. Rank expresses expected impact; the delivery sequence below handles dependencies.

| Rank | Improvement | Expected impact | Effort | Confidence | Existing-plan relationship |
|---|---|---|---|---|---|
| 1 | Reproducible company delivery evaluations | Very high: establishes whether the company can deliver | Large overall; medium first fixture | High for building blocks; live results unmeasured | Extend Phase 1 beyond games |
| 2 | Criterion coverage and persistent findings | Very high: prevents incomplete work appearing complete | Large; small first QA fix | High for specific validator/contract gap | Implement/extend Phase 2 |
| 3 | Generated effective assignment contract | High: reduces policy/tool/context drift | Medium–large | High for ingredients; unified contract planned | Implement/extend Phase 3 |
| 4 | Structured rejection and next-action guidance | High: reduces invalid calls and manual recovery | Medium | High for response-shape comparison | Additional early SDK slice |
| 5 | Unified budgets and progress-aware stopping | High: bounds waste across retries/providers | Medium–large | Medium: enforcement coverage needs tracing | Additional platform/SDK slice |
| 6 | Crash-safe handoffs and integration evidence | High: prevents duplicate effects and stranded work | Medium | High for pattern; local gaps need fault injection | Expand recovery scenarios |
| 7 | One assignment-readiness report | Medium–high: reduces setup/grant failures | Medium | High for distributed prerequisites | Accelerate Phase 5 diagnostics |
| 8 | Curated delivery playbooks | Medium: improves repeated work | Medium | High for concept; gains unmeasured | Implement Phase 4 playbooks |
| 9 | Bounded proactive programs | Medium later; low before reliable delivery | Medium–large | High for registry pattern | Implement Phase 4 programs last |

### 1. Prove the whole company workflow with reproducible evaluations

**Learn from RoboCo.** Its evaluation runner reuses a disposable database/project/fake-forge harness and can drive actual agent spawning. A scripted alternative exercises real lifecycle tool functions. It records completion, revisions, time, tokens and cost, keeping model-judge scores explicitly nondeterministic. [R2]

**Improve the design.** The benchmark is developer-cohort and leaf-task focused. `FixtureResult.passed` is based on completed status and absence of a stall; that alone is not independent proof of correct software. Evaluate both workflow integrity and built-product behavior, with expectations outside the agent-editable repository. Do not let the system grade itself solely by reaching its own terminal state.

**Evaluation tooling implementation:** extend the reliability plan with a small software project passing through PM scope approval, architecture, delegation, development, independent QA, revision, and governed integration. Start with scripted model responses through real application services. Add a separate certified-Office/real-model profile recording agent package, model configuration, policy revision, toolchain image, repository SHA and artifact digest. SDK callback tests remain the fast inner layer. [C1–C4]

**Acceptance:** one deterministic complete-company scenario plus rejection, restart, stale-approval and revoked-grant variants. Inject failure after an external effect and before acknowledgement; assert zero duplicate effects. Test output behavior, not only task state. Record verified completion rate, escaped defects, manager interventions, revisions, wall time and cost per verified delivery. Report deterministic, live-runtime and model-quality results separately.

### 2. Make requirement coverage and findings authoritative

**Learn from RoboCo.** QA names task criteria and supplies evidence; unknown and uncovered criteria are rejected. Decomposition maps child work to parent criteria. A findings repository retains Open, Addressed, Verified and Waived states across rounds. [R3–R5]

**Concrete C-Sweet gap:** `SoftwareQaAgent.ValidateOutcome` checks `outcome.Criteria.Count == brief.AcceptanceCriteria.Count`, validation presence, status/exit-code consistency and findings. It does not compare criterion identities/text against the brief. For criteria A and B, two “A passed” results satisfy that method's coverage-count condition. This is a code-level validator finding, **not** a demonstrated end-to-end merge bypass. Add exact membership, uniqueness and nonempty-evidence checks immediately, then enforce the invariant in the trusted result-acceptance path. An untrusted agent's self-validation is insufficient. [C5]

**Conditional shared primitive:** first model this through existing evidence/review APIs and an optional domain extension. Only after satisfying the core-admission rule, add stable criterion IDs scoped to an approved planning revision, parent-child coverage claims, owners, verification requirements and immutable evidence references. Add a finding ledger with stable ID, originating review, criterion/artifact/commit references, severity, state, author, verifier and append-only history. Adapt existing `QualityFinding` and `ReviewFinding` inputs while retaining provenance.

**SDK placement:** domain operations initially belong in an optional extension library; only admitted shared contracts enter the base SDK. Provide typed read/address/verify/waive operations with separate grants, expected revision and stable idempotency keys. Implementers report fixes; verification requires independent authority. Waivers record authorized decisions and rationale. Blocking Addressed findings remain blockers until verified or explicitly waived.

**Acceptance:** duplicate/unknown/missing criteria fail; changed criteria or reviewed revisions invalidate applicable coverage; parent completion requires current evidence; replay does not duplicate findings; failed fixes reopen the same finding; self-verification and cross-organization references are denied.

**Avoid compatibility shortcuts:** RoboCo's parent roll-up helper is inactive when coverage has never been declared, and treats unexpected non-list results as no rejection. Mark legacy coverage unknown and require explicit migration/approval; absent or malformed evidence is not success. [R4]

### 3. Generate the assignment the agent is actually allowed to execute

**Learn from RoboCo.** Canonical lifecycle policy feeds role verbs and generated Markdown/JSON/prompt artifacts through deterministic generation. This connects documentation to executable policy. [R6]

**C-Sweet delta:** `WorkExecutionAssignmentV1` already pins policy, stage, assignment revision, attempt, deadline, prior outcomes and evidence. The SDK has role profiles and current grants. Combine those inputs into one effective assignment projection, not another policy source. [C1, C4]

Include generic objectives, approved completion requirements, revision-bound evidence, permitted outcomes, tool descriptors, budgets, escalation/wait paths and allowed actions. Domain extensions supply criterion/finding detail and source/build/PR references through versioned payloads; core must not require software-specific fields. Render the same canonical model for agents, authorized operators and lifecycle documentation. Include an audit digest and stale-outcome validation.

**Boundary:** a dispatch snapshot never extends grant lifetime. Revalidate current authorization on every effect. Distinguish pinned workflow inputs from revocable authority; a stale digest triggers refresh/reconciliation, not automatic permission restoration.

**Acceptance:** equal pinned inputs produce equal bytes; unauthorized tools are absent; renderers agree; revocation blocks execution despite a previously valid contract; stale outcomes cannot advance work; CI detects generated-document drift. Preserve V1 compatibility and explicitly negotiate V2 assignment/outcome support.

### 4. Tell agents why an action failed and what can happen next

**Learn from RoboCo.** Its envelope carries missing fields, field hints, remediation, current state and valid next verbs. It distinguishes bad input, missing evidence and invalid state. [R7]

**C-Sweet delta:** `PlatformCapabilityException` already carries capability, error code, optional failure code and retryability. Extend that shape with structured field errors, blocker references, retry-after/event information, current revision, authorized next actions and effect status. Preserve typed SDK APIs. [C4]

Remediation must use server-owned action identifiers and bounded arguments, not executable commands or arbitrary retrieved prose. Filter actions by live scope/grants and authorize again when invoked. Distinguish `NotApplied`, `Applied`, and `OutcomeUnknown` so a timeout cannot trigger a repeated material action without reconciliation.

**First slice:** one denied work transition and one stale-revision Git/QA operation. Extend SDK test fixtures to inject these outcomes.

**Acceptance:** missing criteria return identifiers; stale revisions prompt refresh; denied authority never widens grants; identical nonretryable calls stop within policy limits. Compare invalid-call count and operator rescues against a baseline before claiming gains.

### 5. Unify budgets and stop loops based on lack of progress

**Learn from RoboCo.** Claim guards check project spend caps; runtime sweeps address task/session limits and provider overload; recovery distinguishes some provider failures from ordinary crashes. Stop spending on failures another spawn cannot fix. [R8]

**C-Sweet baseline:** queues, inference waiting, provider/model approval, token accounting, financial budgets and bounded attempts exist. Inspected paths do not establish a single durable budget spanning parent/child tasks, model calls, tool jobs and repeated attempts. That is an integration question to verify, not proof that all cost control is absent. [C3, C6]

Add work/parent/organization accounting at trusted dispatch/provider boundaries: reserve an upper bound where practical, reconcile usage, refund unused reservations, and prevent concurrent requests spending the same remainder. Track money, tokens and local compute/runtime separately. Unknown cost must be explicit; require a governed exception when an operation cannot be bounded beforehand.

Use typed provider failures, shared cooldowns and bounded probes. Detect cycles through repeated state, rejection code, source/evidence digest and lack of verified progress. Attempt/traversal limits remain the backstop. Preserve acknowledged inference waiting versus active execution time.

**Acceptance:** workers cannot over-reserve caps; restarts cannot reset spend/loop state; outages do not cause retry storms; stopping records a checkpoint and next resolver. Agents cannot raise limits. Model/provider substitution stays approved and traceable, never an escape from budgets or privacy policy.

### 6. Make handoffs recoverable across database and external effects

**Learn from both systems.** RoboCo composes lifecycle actions in a database savepoint, but some Git effects precede it and others follow it. Its response model recognizes that state can advance while a handoff fails. C-Sweet has durable inbox and merge jobs; use them to reconcile each boundary. [R9, C2, C3]

For advancement, publication, review, notification and merge, persist an effect intent with a stable domain key. After timeout/restart, query the existing effect before issuing another. Keep transitions and outbound intents transactional where possible; where providers lack idempotency, use object correlation and reconciliation. Atomic database writes do not establish exactly-once delivery.

Test integration beyond isolated branches: reviewed child commits can behave differently together. Define evidence for the assembled candidate and invalidate it when the candidate or required policy changes. Reuse trusted Git, exact-SHA rules and toolchain evidence rather than a fixed developer-to-cell-to-root hierarchy.

**Acceptance:** crash before/after PR creation, QA recording, notification, merge and acknowledgement; no duplicate effects; unknown outcomes reconcile; cancelled/reassigned work cannot complete superseded attempts; changed assembled candidates require fresh applicable validation.

### 7. Show one actionable readiness report before dispatch

**Learn from RoboCo.** Bootstrap includes readiness polling and diagnostic failures instead of equating started containers with a ready company. Borrow the experience, not the topology. [R10]

C-Sweet needs coherent installation, models, grants, repository policy, toolchains and Office certification. Aggregate preflight/readiness into an assignment-specific report with stable codes, responsible operator, remediation link and last-checked time. Reuse Phase 5 probes and current setup flows. [C1, C8]

Bound expensive probes and make freshness explicit. Discovery must not expose other organizations. A green indicator never overrides dispatch-time authorization/certification.

**Acceptance:** a disposable developer reaches verified delivery through documented setup; missing prerequisites identify the right owner/action; revoked certification fails closed; stale diagnostics never authorize work. Measure time to first verified delivery and setup failures.

### 8. Turn verified outcomes into curated playbooks

**Learn from RoboCo.** It separates drafts from approved procedures, distills short completion lessons, and records source-program provenance. [R11]

Use existing memory and trust/confirmation states. Delivery can propose procedures with evidence, applicability, toolchain/model versions, failure cases, owner and review date. Independent approval promotes them; supersession/expiry stops stale recall. Procedures remain advisory: they cannot grant access, change assignments, waive findings or authorize spending. [C7]

**SDK:** bounded retrieval of approved applicable procedures, structured feedback and evidence-backed candidates. Retrieve for the task/role instead of inserting the whole knowledge base each turn.

**Acceptance:** unapproved/out-of-scope procedures never enter approved context; promoted lessons trace to verified outcomes; contradictions/supersession are visible; repeated-task evaluations measure quality/cost benefits. Model-generated lessons are candidates, not established facts.

### 9. Add proactive programs only after delivery is reliable

**Learn from RoboCo.** A registry describes periodic/event/metric-triggered programs with role, scope and per-cycle limits. Several produce held proposals for human review. [R12]

Use existing attention/reconciliation. Start with one opt-in report-only program: repeated build failures, unresolved findings, stale dependencies or documentation drift. Give it a dedicated identity with no default grants, versioned definition, occurrence key, cooldown, output cap, budget and owner.

**Acceptance:** repeated events create one occurrence; unchanged state stays quiet; observe-only makes no material changes; revocation stops activity; no unbounded program chains. Proposals enter normal approval/assignment paths. Avoid the full catalog before one program produces useful output without noise. [C4, existing Phase 4 plan]

## Security: where C-Sweet is stronger, and where to improve

The code supports a **stronger untrusted-execution boundary** in C-Sweet. It does not establish a blanket security ranking across every feature or deployment.

| Boundary | RoboCo evidence | C-Sweet evidence and implication |
|---|---|---|
| Guest/host isolation | Docker agents use an agent network, authentication mounts and shared workspace-root mount. The orchestrator has the Docker socket; this does not mean every agent receives it. [R13] | Office verifies signed workload identity/specification/expiry/fencing and selects certified hardware isolation; Hyper-V removes network adapters. Preserve separation. [C8] |
| Secrets/workspaces | Claude authentication is mounted into agents; CLI rules attempt to deny credential reads. The mount function exposes the workspace root, while role permissions restrict normal tools. [R13] | Credentials/transport stay private; brokered Git governs repository access. Withholding secrets/filesystems is stronger than tool-deny patterns. [C2, C4] |
| Authentication | HMAC and CEO-session paths exist. Enforcement is configuration-dependent: source Compose defaults cloud auth on; non-cloud development paths can make agent tokens optional. [R14] | Retain authenticated installation-bound calls and live grants. Do not characterize RoboCo as universally unauthenticated. |
| Prompt injection | Regex detection and external-text wrappers offer labeling with predictable pattern limits. [R15] | Treat repo files, tool output, memory and messages as untrusted. Test authority boundaries even if models follow hostile text; VMs do not prevent misuse of granted capabilities. [C8] |
| Evidence availability | Failed-review SHA capture returns no SHA on lookup failure so the unchanged-resubmission guard can fail open. This is an anti-repeat review guard, not proof of authorization bypass. [R16] | Missing required approval/evidence must block or reconcile. Retrieval error cannot mean approved. Preserve exact-SHA authorization. [C2] |

Prioritize integrated negative tests: forged/duplicate criterion evidence; implementer self-verification; stale approval after code changes; cross-tenant artifact/criterion IDs; mid-run grant revocation; hostile repo instructions requesting external capabilities; replay after crashes. Include them in ranks 1, 2 and 6 rather than creating a security agent whose opinion overrides policy.

Test broker data flows too. Origin/path and private-address policy primitives exist; verify each new connector/toolchain capability applies redirect, DNS, destination and response-size checks. Strong isolation does not eliminate SSRF, confused-deputy behavior, misleading evidence or overscoped tools. This review did not validate every connector path. [C9]

## Weak points to avoid adopting

- **Organization size as success.** Twenty-five roles do not prove delivery quality. Keep configurable staffing and justify stages with outcomes.
- **Rigid paperwork for every task.** Require evidence, but choose stages by work type/risk through policy rather than ceremonial handoffs.
- **Large central runtime ownership.** RoboCo's very large orchestrator handles providers, recovery, budgets and programs. Borrow bounded services/registries, not the whole structure. This is a maintainability concern, not proof of defects. [R13]
- **Completion, model judges or prose as correctness.** Use independent behavior tests and immutable provenance. [R2]
- **Fail-open compatibility for new governance.** Unknown coverage, failed lookup and unexpected types remain explicit unknowns. [R4, R16]
- **Assuming transactions cover remote effects.** Persist intent and reconcile remote state. [R9]
- **Credentials or CLI deny rules as the sandbox.** Keep secrets and host authority outside guests. [R13]
- **Unlimited self-improvement.** Curate lessons, cap proactive work and verify benefit. [R11, R12]

## SDK and contract ownership

These are proposals, not currently available API names. The placement table above governs these candidate surfaces: optional domain contracts do not belong in WorkManagement.Contracts or the base SDK merely because several software agents share them. Headquarters owns generic authority/persistence; extensions own domain behavior and use governed platform services.

| Proposed surface | Owner | SDK responsibility | Platform responsibility |
|---|---|---|---|
| Effective assignment/digest | WorkManagement.Contracts + projection service | Typed helpers; negotiated V2 | Bind revisions, compute projection, validate outcomes/live authority |
| Criteria/finding ledger | Extension contracts first; shared contracts only after core admission | Typed read/address/verify/waive | Identity, independent authority, concurrency, history, evidence |
| Actionable failures | Shared error contract + SDK | Preserve structured errors/action hints | Safe scoped errors; reauthorize actions |
| Budget/stop/checkpoint metadata | Runtime/work contracts | Read state, honor cancellation, publish domain checkpoint | Reserve/reconcile usage, enforce limits, persist recovery |
| Scenario fixtures/failure injection | SDK helpers + evaluation project | Callback fixtures without transport exposure | Real persistence, policy, broker, Office and external boundaries |
| Procedures/feedback | Optional extension over existing memory contracts/service | Bounded typed recall/feedback | Curation, provenance, scope, retention, trust |

Never expose workload/session tokens, leases, raw transport, credentials or caller-selected installation identity. Test the broker with hostile/custom agents that ignore the SDK.

Use additive versioned contracts and explicit negotiation rather than incompatible edits to positional V1 records. WorkManagement.Contracts/SDK changes require semantic version increments, synchronized consumer/documentation/template/test pins, build/test/pack, `.nupkg` metadata verification, and consumer checks with sibling references disabled. Office.Contracts changes also require updating and independently verifying Headquarters and Office. Resolve the SDK checkout-path discrepancy before implementation.

## Delivery sequence and investment decisions

1. **Baseline and immediate correction:** fix exact-criterion validation and its trusted counterpart; add the first complete software-delivery evaluation. Establish failure/operator-intervention baselines.
2. **Delivery integrity:** apply the core-admission rule before Phase 2/3: pilot domain criteria/findings in extensions, reuse generic evidence, and add only the shared assignment projection to core. Add structured failures to participating SDK operations. Keep legacy/V2 behavior explicit.
3. **Bounded operation:** test crash windows, concurrent budgets, outages and integration-candidate changes. Deliver assignment readiness so failures are diagnosable.
4. **Learning pilot:** approve a few procedures for one project; compare results with/without recall. Launch one report-only program after preceding scenarios pass.

Each slice needs a canary cohort, independent rollout controls and rollback preserving history. Flags control rollout, not authorization. Expand after objective gates pass; ranking does not commit the project to every item.

The next investment should be **an external software-delivery evaluation and the software QA coverage fix**, using existing platform mechanisms. The clearest shared-platform candidates are actionable failures, assignment projections and reliable resource/effect accounting. Pilot findings, playbooks and proactive programs as optional extensions; promote only the smallest primitives whose broad applicability is established.

## Source references

RoboCo links are pinned to the inspected commit. They support implementation observations, not production-result claims. This analysis paraphrases designs and imports no code. RoboCo declares AGPL-3.0 in its [license](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/LICENSE); later source reuse needs a separate compatibility decision.

- **R1:** [README](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/README.md).
- **R2:** [Evaluation runner](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/eval/runner.py), [fixtures](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/eval/fixtures.py), [scripted bench test](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/tests/e2e_smoke/test_eval_bench.py). See `FixtureResult.passed` and module scope limits.
- **R3:** [QA gates](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/choreographer/qa.py#L731), `_validate_criteria_verified`.
- **R4:** [Choreographer](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/choreographer/_impl.py#L1490), `_parent_acs_covered_envelope` and delegation/coverage helpers; [coverage tests](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/tests/unit/gateway/test_delegate_ac_coverage_gate.py).
- **R5:** [Finding repository](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/repositories/review_findings.py), [finding validation](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/choreographer/findings.py).
- **R6:** [Lifecycle policy](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/foundation/policy/lifecycle.py), [role configuration](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/role_config.py), [generators](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/foundation/_generators.py).
- **R7:** [Response envelope](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/envelope.py).
- **R8:** [Claim guards](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/claim_guards.py), [budget sweeps](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/runtime/orchestrator.py#L8751).
- **R9:** [Verb runner](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/choreographer/_verb_runner.py), `run_intent` / `_run_composed_actions`; response warnings in R7.
- **R10:** [Bootstrap readiness](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/scripts/bootstrap.sh).
- **R11:** [Playbooks](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/playbook.py), [lesson distiller](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/memory_distiller.py).
- **R12:** [Program registry](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/foundation/policy/board_programs.py).
- **R13:** [Runtime mounts](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/runtime/orchestrator.py#L3325), `_build_mount_args` / `_core_volume_and_env_args` and CLI permission generation; [Compose](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/docker-compose.yml#L780).
- **R14:** [Authentication](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/api/deps.py), `_check_agent_auth_token` / `_cloud_auth_agent_context`; defaults in R13 Compose.
- **R15:** [Injection guard](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/foundation/policy/injection_guard.py).
- **R16:** [PR gate](https://github.com/rennf93/roboco/blob/fc01630ef36413d79c4296ed8243e7940e19e676/roboco/services/gateway/choreographer/pr_gate.py#L1112), `_capture_pr_head_sha`.

C-Sweet links are relative to this multi-repository workspace. Companion hashes identify the inspected working-tree files.

- **C1:** [Orchestration](../../src/CSweet.Infrastructure/WorkManagement/WorkOrchestrationService.cs), [policy validator](../../src/CSweet.Infrastructure/WorkManagement/WorkOrchestrationPolicyValidator.cs), [assignments](../../../CSweet.WorkManagement.Contracts/src/CSweet.WorkManagement.Contracts/WorkOrchestrationContracts.cs), [retry tests](../../tests/CSweet.UnitTests/WorkOrchestrationRetryTests.cs).
- **C2:** [Merge executor](../../src/CSweet.Infrastructure/WorkManagement/GovernedMergeWorkActionExecutor.cs), [software assignments](../../src/CSweet.Infrastructure/WorkManagement/SoftwareDevelopmentWorkService.cs).
- **C3:** [Inbox](../../src/CSweet.Infrastructure/Setup/AgentWorkInbox.cs), [tests](../../tests/CSweet.UnitTests/AgentWorkInboxTests.cs).
- **C4:** [SDK operating contract](../../../CSweet.Agent.Sdk/docs/agent-operating-contract.md), [client and exception](../../../CSweet.Agent.Sdk/src/CSweet.Agent.SDK/PlatformCapabilityClient.cs), [test runtime](../../../CSweet.Agent.Sdk/src/CSweet.Agent.SDK/AgentTestRuntime.cs), [contributor boundaries](../../../CSweet.Agent.Sdk/AGENTS.md).
- **C5:** [QA implementation](../../../CSweet.Agent.SoftwareQA/src/CSweet.Agents.SoftwareQA/SoftwareQaAgent.cs), `ValidateOutcome` at line 241; [planning/quality contracts](../../../CSweet.WorkManagement.Contracts/src/CSweet.WorkManagement.Contracts/WorkManagementContracts.cs), [artifact reviews](../../../CSweet.WorkManagement.Contracts/src/CSweet.WorkManagement.Contracts/WorkstreamProfileContracts.cs), [delivery records](../../src/CSweet.Domain/Core/DeliveryEvidence.cs).
- **C6:** [LLM jobs](../../src/CSweet.AgentHost/Broker/PlatformLlmJobService.cs), [LLM handler](../../src/CSweet.AgentHost/Broker/PlatformLlmCapabilityHandler.cs), [usage](../../src/CSweet.Infrastructure/Llm/LlmTokenUsageService.cs), [budget reservations](../../src/CSweet.AgentHost/Broker/WorkforcePlatformCapabilityHandler.cs), [inference waits](../implementation/llm-queue-and-startup-recovery.md).
- **C7:** [Memory](../../src/CSweet.Infrastructure/Core/AgentMemoryService.cs), [tests](../../tests/CSweet.UnitTests/AgentMemoryServiceTests.cs).
- **C8:** [Privileged authorization](../../../CSweet.Office/src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs), [provider selector](../../../CSweet.Office/src/CSweet.Office.Runtime.Core/FailClosedIsolationProviderSelector.cs), [Hyper-V provisioning](../../../CSweet.Office/src/CSweet.Office.Runtime.HyperV.Helper/PowerShellHyperV.cs), [threat model](../../Documentation/Security/AGENT_RUNTIME_THREAT_MODEL.md).
- **C9:** [Network policy](../../src/CSweet.Infrastructure/Setup/OutboundNetworkPolicy.cs), [capability catalog](../../src/CSweet.AgentHost/Broker/McpToolCatalog.cs), [evidence safety tests](../../tests/CSweet.UnitTests/DeliveryEvidenceSafetyTests.cs).
