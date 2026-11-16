# QFN Games / Shardfall: Sprint 1 flow check, November 16

**Sprint 1 started and produced real work, then stalled in document review.** At the final read, **12:16:51 p.m. PST** (`2026-11-16T20:16:51Z`), it remains Active with capacity eight, one committed item and zero completed items. Its original planning-only feasibility task is Blocked at quality review, revision 43, unchanged since **12:08:10**. This is a new execution/review problem; the zero-capacity failure has not returned.

This is a read-only follow-up to the [capacity recovery observation](qfn-games-shardfall-2026-11-16.md). No retries, decision answers, calendar edits, agent updates or implementation changes were made during this inspection. Local evidence is retained in `artifacts/qfn-sprint1-flow-review-2026-11-16.json`.

## Verified delivery history

Organization `40096d7b-579e-4481-9da9-2e7e1e4ef4cc`; project `b434f434-0df4-46a0-8d78-3293d4523f52`; board `5d0242b5-825e-4c65-9072-080da45491df`; sprint `d0bce325-6ae5-49e8-a3ac-ff063d187838`; task `ea08b8f6-fa1e-4304-9f22-0137a9b07ab8` (`VGB434F4340D-15`). Sprint execution is `91efa91b-dbb7-482f-bc48-9126322ed509`.

Victor submitted six distinct technical-design documents between **11:58:00 a.m. and 12:07:35 p.m.** Gabriel reviewed each. The final review stopped the rework loop at **12:08:10** after three consecutive cycles without improvement over the best retained acceptance result. The first five exposed review outputs show 6/9, 7/9, 8/9, 8/9 and 7/9 criteria satisfied, respectively. Different criteria fail in the two 8/9 results; the later result does not preserve the earlier satisfied set. The stagnation stop is useful protection against further blind retries, not the original cause of failure.

The latest document is `0462bf1f-7c94-42e4-8d67-7b157260d9ed`, revision `b7e3538c-6738-492b-b40e-2bad1ff53e00`, SHA-256 `00f976fc99df235d10844782db1f9fc354af150d522810f406191f6b0d536742`. It is InReview, not accepted. Its body is 40,802 characters. It contains substantial decomposition work, but explicitly states that all package-suitability cells remain unanswered questions with no supporting independent evidence.

## 1. Author and reviewer do not receive a shared, usable evidence packet

The assignment requires citations to numbered Producer, Game Director and Creative Director sources. The author receives the accepted production plan and the complete production-sources document, including pitch and GDD bodies. The sources are **not missing altogether**: the production-sources body has 36,104 characters. Its JSON header intentionally leaves content empty and stores the original bodies below length-delimited headings. `ProductionSources.Serialize` and `Parse` implement this format.

However, the execution prompt does not provide the canonical numbered source register that the planning task demands. The author repeatedly records the unavailable crosswalk as a blocker. More seriously, SDK `ArtifactDeliveryReview.ExecuteAsync` retrieves the delivered document and current planning, then sends the model `item.Planning`, `revision.Content` and `assignment.Evidence`. It does not retrieve the accepted source-package bodies for that review. The reviewer repeatedly says it cannot independently check the supplied source IDs against the cited text.

**Consequence:** rewriting the document cannot supply evidence absent from the review packet. Requiring exact internal index formatting compounds the handoff problem.

**Repair direction:** resolve authorized source references into a common, hash-checked evidence packet; retain stable citations through planning and execution. Code should produce citation identities and crosswalks. Inference should decide whether the cited content supports the required outcome, and reject material missing support rather than an arbitrary citation presentation.

Source map: Producer `ProductionReviewContracts.cs` / `ProductionSources`; Technical Director `extensions/video-game/VideoGameAgentKit.cs` / `VideoGameSpecialistAgentBase.ExecuteCapabilityCoreAsync`; SDK `ArtifactDeliveryReview.cs` / `ExecuteAsync`.

## 2. Evidence retrieval is absent, while calendar tools become an escalation workaround

The first reviews correctly challenge unsupported package/API assertions and a typo. Later revisions respond by converting every capability claim into an unresolved question, which no longer establishes the required technically suitable alternative. Calling these questions a feasibility evaluation cannot satisfy the accepted outcome. Browser/build tests are out of scope for this increment, while public documentation is allowed; supplying relevant authoritative documentation is the appropriate separable evidence work.

The document execution harness calls `context.Platform.Calendar.GetResponseAsync` with ordinary response options. This adds the approved calendar tools, not research, artifact-reading or decision tools. The QA model receives no tools. There is no evidence-retrieval step in either path.

The live author used the tools it did have to schedule two “escalations,” then repeatedly updated them. The IDs its latest document describes as manager-owned decision records are actually calendar events:

- ESC-01 `b2c2f76e-2a97-4f17-94ae-6513df6ecbab`, revision 2, scheduled for November 17 at 2 a.m. PST.
- ESC-02 `ea4c4baa-dd0c-469d-9d5d-7bc9a127745d`, revision 3, scheduled for November 17 at 3 a.m. PST.

Both are owned by Victor, target instructions back to Victor, and list Marcus as an attendee. Their instructions ask Victor to relay the request and record an answer later. They are not native project decision records or an immediate decision dependency. The project inspection reports **zero open decisions**. This is tool substitution and misleading workflow language, not a fabricated event ID: the events genuinely exist.

**Repair direction:** provide a reusable, authorized evidence/research path and an inference-driven way to request missing information or decisions from the accountable owner. Confirm the actual effect type before reporting that an escalation was created. A scheduling tool should serve a real scheduling need, rather than substitute for a blocked-work protocol.

Source map: Technical Director `VideoGameAgentKit.cs` / `ExecuteCapabilityCoreAsync`; SDK `PlatformCalendarClient.cs` / `GetResponseAsync`, `WithToolsAsync`; SDK `ArtifactDeliveryReview.ExecuteAsync`.

## 3. Planning escalations keep diagnosing the already-repaired failure

The project contains eight Decided management-direction records about reconciling or resuming the old comment/execution conflict. Two were created after Sprint 1 started. The latest, `f82ca68b-9d20-4131-833b-f6e099e5a404`, was created at **12:13:48** and answered at **12:14:04**. It still diagnoses a comment-state conflict instead of the actual failed source/package-evidence review. Its evidence includes old advice and earlier direction; it does not identify the six delivered documents as the current work product.

Producer `ReviewAdaptivePlanAsync` includes board item status, delivery-plan facts, prior Decided directions and retained design advice. It adds sprint orchestration outcomes inside `Decide` only when passed a sprint; that path handles **completed** sprints. The current-plan call uses `Decide(key, null)`. It therefore lacks equivalent current active-sprint artifact-review outcomes. New direction becomes evidence for another assessment. The action intent digest also includes rationale wording and evidence IDs, so paraphrasing and accumulating earlier decisions can evade semantic deduplication. This mechanism explains the observed repetition; it does not establish that every callback is unnecessary.

This path also still uses the fixed “Provide authoritative direction / Continue within accepted scope / Request more evidence” menu in `PlanningDecisionRequests`, despite the newer inference-authored decision support elsewhere.

**Repair direction:** reconcile current execution and accepted effects before retaining an old diagnosis; supersede obsolete blockers; reuse one semantic decision across paraphrases; infer choices and concrete follow-through from the actual remaining evidence gap. Preserve independent acceptance and genuine authority requirements.

Source map: Producer `AdaptiveSprintReview.cs` / `ReviewAdaptivePlanAsync`, `ApplyReviewEffectsAsync`; `PlanningDecisions.cs` / `PlanningDecisionRequests`.

## 4. Rework creates another document rather than resolving the dependency

Every author traversal calls `Artifacts.CreateAsync` with a stage/attempt key. The six submissions are six artifact containers, each at revision one. On the first pass the body claims publication is blocked even though code then submits it. On the next pass it names the previous container while code creates another. These contradictions consume two review cycles; later documents remove self-identity language.

Subsequent rework repeatedly regenerates content without obtaining documentation or the citation crosswalk. The author eventually says the gaps are not resolvable by another rewrite but still returns a successful document delivery. Code constructs `remainingRisks: []` without a judgment about these unresolved dependencies.

**Repair direction:** code owns publication status and identity; rework should retain the current artifact and explicitly address the current findings. Let inference distinguish a fixable document defect from missing external evidence or an owner decision, then create that resolution work before another full rewrite. Do not solve this by raising the rework limit or weakening acceptance.

Source map: Technical Director `VideoGameAgentKit.cs` / `ExecuteCapabilityCoreAsync`; platform `WorkReworkPolicy.Evaluate`.

## 5. The stagnation stop loses structured findings and waits before escalation

The final stage is Blocked with `lastOutcomeCode: changes_requested`, a detailed `lastSummary`, and the stagnation reason. Its `latestOutcome` is **null**. `WorkOrchestrationService.ReadCompletedOutcome` returns an outcome only when the **stage** is Completed; the rework policy changes this stage to Blocked after its review completes. Thus the normal execution read hides that completed review's structured criteria, findings and evidence precisely when recovery needs them most.

Producer `DecisionEscalation` requires a blocked outcome carrying `DecisionRequired`; it does not recognize this host-generated stop. The fallback `UnhandledBlockerEscalation` waits 20 minutes. The live recovery task runs at **12:08:20**, then waits until **12:28:20**, with no decision dependency or waiting-on owner. The platform creates a board-manager notification immediately, but that is not an actionable current diagnosis routed through the manager chain. The later planning decision in issue 3 discusses the wrong cause.

**Repair direction:** expose the retained completed review independently of current stage status, represent the stagnation stop as an owned resolution dependency, and wake the accountable manager immediately with the actual findings. Keep fallback discovery and idempotent effects for offline recovery.

Source map: platform `WorkOrchestrationService.cs` / `ReadCompletedOutcome`, `ToResponse`; `WorkReworkPolicy.cs` / `QueueEscalationAsync`; Producer `DeliveryRecovery.cs` / `DecisionEscalation`, `UnhandledBlockerEscalation`; `WorkflowStaffing.cs` / `ReconcileWorkflowRecoveryAsync`.

## 6. Measured coordination and context overhead are substantial

All timeline pages were read to `nextCursor: null` for the three involved agents over **11:56:12 a.m.–12:12 p.m. PST**. Capability counts below are distinct command correlation IDs, not doubled started/completed event counts. Model counts include only provider-start records with outcome Running, excluding queued/generating mirrors.

| Agent | Capability calls | Provider requests |
|---|---:|---:|
| Victor | 328 | 17 |
| Gabriel | 1,857 | 9 |
| Marcus | 448 | 2 |
| Total | 2,633 | 28 |

Gabriel alone reads personal work 305 times, operating state 296 times, sprints 215 times and the roster 177 times. He completes 21 project-health callbacks in this window. Marcus completes 24 recovery-change callbacks. Some reads enforce necessary authority and freshness; the totals are an overhead signal, not proof that each is redundant.

The latest author provider call records **216,624 message characters**, plus 6,501 instruction and 6,328 tool characters. The final reviewer call records **81,988 message characters** and no tools. These are character counts, not token counts. Full source bodies, growing ticket discussion and rework history are repeatedly submitted. The latest artifact is roughly 40 KB despite the role prompt asking for concise sections. The combination spends inference on restating history while evidence acquisition remains unavailable.

**Repair direction:** coalesce wakes around material state changes, use bounded current-state discovery, preserve current authority checks at effect time, and provide a compact current findings/evidence view with retrievable history. Avoid a full planning/health scan for every bookkeeping event. Do not replace event recovery with polling.

## Recommended order and limits

First repair the evidence packet and proper blocker routing (issues 1, 2 and 5). Then reconcile the existing task against the actual latest review, retain the useful document, gather the missing bounded documentation/citation evidence and conduct one informed independent review. Correct stale planning decisions and semantic deduplication alongside that work. Address callback/context overhead using the measured baseline.

The observed active sprint is genuine, but **Active does not currently mean progressing**. No artifact acceptance, completed sprint item or game implementation was observed. This inspection does not waive the requirement to establish a suitable alternative, claim that a future Engineer is unnecessary, or prescribe a fixed game-specific decision flow. Validation: live read-only evidence and source tracing; no implementation or tests were run for this report.


## Implementation and rollout follow-up

The six repair paths are implemented in [document evidence and owned rework recovery](../features/work-management/document-evidence-recovery.md). SDK 5.38.0 published successfully; Technical Director 6.13.0 and Producer 6.21.0 passed their release workflows and were installed through Installed Agents. Both existing hired installations advanced to revision 4 with successful prebuilt-bundle builds. No installed payload or approved package record was patched.

The local platform was rebuilt and restarted, retaining the existing database and Sprint 1 execution. A live read now exposes the previously hidden completed review on stage `e3a3d490-b8e7-4b4e-a5cf-43975e2a37b5`, including all nine structured criteria. Acceptance requirements remain intact. This is verified recovery infrastructure, not yet completed planning work.

The Office connection went offline during restart (last heartbeat 1:08 p.m. PST). Its Windows services report Running, but the control plane cannot dispatch work. The normal Office care repair was opened for the existing identity; it requests Windows administrator approval. The terminal session cannot stop the Office service. Final live acceptance verification is waiting for that connection recovery; the planning task remains blocked until the normal authorized retry and independent review complete.
