# Independent review of the Prism Break sprint planning stall

**Decision requested.** How should C-Sweet let its Producer move eligible work forward when one planning judgment conflicts with a previously recorded decision? Evaluate both the smallest fix for this run and the broader proposal to delegate more planning decisions to agents. Challenge the diagnosis and recommendations where the evidence warrants it.

**Case.** QFN Games started Prism Break, a desktop-web Breakout clone. The team formed, the brief was accepted, and the project board acquired 11 epics. The owner expected the team to decompose the first increment into Stories and tasks and start Production Sprint 1 without a manual nudge. Gabriel Reyes is the Producer and board manager. Victor Lin is the Technical Director who drafts the technical work. Marcus Vale is the Video Game Director and Gabriel's reporting manager. Hana Mori is the Game Designer.

**Last verified live state.** At **9:35 p.m. Pacific on November 16, 2026**, the board had **11 epics, zero published Stories and zero published tasks**. Sprint 1 was **Planned**, with zero items, no capacity value and no start timestamp. Victor had already delivered six task briefs. Gabriel's planning commitment was Blocked and Marcus owned its recovery. Producer 6.21.2 was installed and had run. This document makes no claim about subsequent live progress.

## 1. What should happen from epics to sprint start

The project uses adaptive game planning. Only the approaching increment needs detailed tasks. A provisional sprint can exist before the team has committed any work to it.

| Handoff | Responsible participants and expected effect | Evidence that the step happened |
|---|---|---|
| 1. Outline scope | Gabriel commissions Victor to decompose the accepted brief. Gabriel reconciles ownership and publishes the epic outline. | Completed outline session and project-board epic IDs. |
| 2. Select the first Story | Gabriel weighs dependencies, approved direction, roster and resources, then commissions a specific Story from Victor. | Board-scoped request naming the Story and the outline it belongs to. |
| 3. Return executable detail | Victor maps acceptance criteria to tasks, gives each task an accountable role, records dependencies, and returns the validated briefs. | Completed coordination session containing the technical proposal and its digest. The six task briefs still live in that proposal at this point. |
| 4. Review and publish | Gabriel compares the returned work with the approved scope and current facts. He publishes accepted work or gives Victor a specific correction. | Canonical Story and child task IDs with parent links, criteria and proposal provenance, or a revision request tied to the returned draft. |
| 5. Staff and estimate | Gabriel binds eligible team members to tasks. Assigned owners supply estimates, assumptions and available capacity. Missing coverage may require a smaller scope or a staffing decision. | Ticket estimates linked to their planning revisions, named owners and capacity offers. |
| 6. Commit scope | Gabriel selects work that fits capacity and whose prerequisites are complete or included in the sprint. He checks applicable reviews and holds. | Ready tasks assigned to Sprint 1, a capacity value and a recorded preparation decision. Adaptive planning can seek QA advice when needed. |
| 7. Start execution | Gabriel finalizes delivery specifications and stage assignments and requests preflight. C-Sweet checks authority, workflow, assignments, dependencies and required independence. Gabriel requests start on the current sprint revision. | Sprint status Active, a start timestamp, committed items and an execution record. A worker's first claim is a later milestone. |

## 2. What actually happened

All times below are Pacific on November 16. In the local API these events have UTC dates of November 17.

| Time | Recorded event | What it establishes |
|---|---|---|
| 8:36–8:48 p.m. | Production package accepted at 8:36:45. Sprint 1 created at 8:40:33. Victor returned the outline at 8:46:57. The board showed 11 epics by 8:48:35. | Startup progressed through roadmap publication. An empty Planned sprint was expected at this stage. |
| 8:50–8:56 | Gabriel requested the first guardrails Story at 8:50:07. A separate consultation addressed privacy evidence and final art ownership. At 8:55:04 a delivery-plan read succeeded after nine earlier denials. Victor saved six tasks at 8:55:33 and returned the validated proposal at 8:56:50. | Victor finished his handoff. The earlier read-permission failures no longer explained the delay. |
| 9:00–9:01 | Two Producer assessments selected candidate 0, the same guardrails Story, and said to proceed. Both returned `keepDraft=false`. Their descriptions of the web toolchain changed from Certifying to Ready. | The evidence used for the review changed. The selected Story stayed the same. Toolchain readiness was one visible change among several possible inputs to the review. |
| 9:01–9:05 | Gabriel retained the proposal outside the board and deferred review until 9:16. At 9:05 the dashboard showed Gabriel waiting, Marcus in periodic oversight and Hana waiting for a background claim. | The first missing transition was Gabriel's publication decision. No specialist had an executable project ticket to claim. |
| 9:17–9:20 | Gabriel's planning commitment resumed. Producer 6.21.1 made three attempts to reconcile planning issues. The platform rejected reopening a resolved art-ownership issue. | A second failure appeared upstream of draft review. Retrying the same request reproduced it. |
| 9:27–9:35 | Version 6.21.2 became the active Gabriel installation. At 9:35:26 the same issue update failed under 6.21.2 and Gabriel's commitment became Blocked. Victor's original refinement session remained Completed. | The new version ran, but this attempt stopped before it could exercise the corrected draft review. Marcus's recovery activity did not establish that planning had resumed. |

### What Victor supplied

The selected Story, `roadmap-epic-1.story-1`, concerned scope baselines, exclusions and authority. Its proposed tasks were **Scope Baseline and Traceability Record** (Technical Director), **Binding Non-Goal Rejection Matrix** (Producer), **Authority Guardrail Review** (Producer), **Ownership and Procedural Asset Policy** (Producer), **Publish Versioned Governance Package** (Engineer), and **Independent Governance Artifact Review** (QA). These were proposed roles. The run had yet to demonstrate eligible assignments or capacity for that exact scope. In particular, final art ownership might affect the fourth task. Its impact on individual briefs deserves review.

## 3. Two distinct failures

### A. Gabriel was asked to judge a draft he could not see

**Observed.** The 6.21.1 model requests contained `provisionalStory=null` and `provisional=null` after Victor's completed proposal was available. Gabriel could see the epic, accepted brief, resource context and a summary of Victor's completion. The two decisions selected the same Story and cited reasons to proceed. Neither named a task or criterion that needed changing.

**Code explanation.** The admission path had already read Victor's proposal from the active refinement session. The prompt builder looked for it in fields populated when a prior draft had been set aside. On a first handoff those fields were empty. A changed review basis plus `keepDraft=false` sent the proposal into the same waiting branch used when a different Story is selected. This explains the misleading activity message that the “next Story” had changed. The missing input is verified. The model's reasoning with a complete input on this live case remains unknown.

**Implemented in 6.21.2.** The admission path passes the validated Story definition and its child briefs directly to the reviewer. A saved review decision is bound to its Story, session and artifact digest. Rejecting the draft while selecting the same Story requires a revision reason. The Producer can also read a draft already retained by the earlier version. The original model reply was preserved as a regression fixture. All 602 Producer tests passed against pinned packages. Tests included a resource change, retained-draft recovery and replay without duplicate tickets. The packaged agent passed its self-test and bundle verification. Live installation was verified. Successful live admission and publication remain unverified.

### B. A resolved issue interrupted the entire planning pass

**Observed.** Gabriel requested an `Open` issue for final art-package planning and acceptance ownership against the same approved brief. The platform returned `Conflict` with the message **“A resolved unchanged obligation needs new recorded evidence; rewording cannot reopen it.”** The same failure occurred before and after the Producer update. The dashboard's “execution stopped unexpectedly” message concealed the more specific cause available in the failed capability event.

**Where inference is used.** The ownership model compares approved obligations with epic outcomes, criteria, constraints, approved direction and specialist advice. It judges whether the evidence assigns meaningful responsibility, whether a gap exists, and which scope it affects. The art concern came through that review. The recorded resolution concerns planning ownership. It does not establish that the final art package was produced or accepted.

**Where code takes over.** The Producer reads the issue history, then translates an ownership gap into an `Open` update. It skips duplicates for some already-open issues, but this path submitted a reopening request without fresh evidence acceptable to the platform. On the same approved source, the platform requires a reopening link with a kind and ID absent from the issue's existing links, then validates that link. This check governs issue history. It does not read the art prose and decide whether the art is adequate. Because issue reconciliation runs before Story admission and the conflict stopped the pass, it held up every task regardless of the issue's actual scope.

**Separate content constraint.** Story validation also requires each original Story acceptance criterion to appear verbatim on at least one child task. That rule is relevant to the broader autonomy question. Neither recorded failure was a rejection under that rule.

## 4. Changes proposed for independent evaluation

Only the 6.21.2 draft-input fix above was implemented in this investigation. The following are proposals.

| Proposal | Intended behavior | Tradeoff or failure case to evaluate |
|---|---|---|
| **1. Contain issue conflicts to affected work.** Start here. | On an unsupported reopen, give Gabriel the recorded resolution, its evidence and the current concern. Ask what changed and which tasks it affects. Preserve the resolution if nothing material changed. If new evidence warrants reopening, code submits its verified reference. Gabriel continues reviewing independent work. | A real cross-cutting dependency could be missed. Require a traceable account of affected tasks and their dependents. A conflict within a batch also requires re-reading authoritative state before resubmitting other updates. |
| **2. Give Gabriel an explicit decision over returned work.** | Publish the eligible Story and tasks, request a bounded revision from Victor, or accept a dependency-complete subset and keep omitted obligations visible with an owner. Code binds every action to the exact returned proposal. | Partial publication can strand a criterion, create a dangling dependency, or make a parent appear complete early. Specify how deferred tasks, partial coverage, retries and later revisions retain their identities. |
| **3. Allow semantic coverage across tasks.** | Keep approved criterion text and identity. Let Victor map it to one or more child tasks and let Gabriel judge whether the mapping preserves its meaning. Code verifies references and coverage. A keyboard-and-mouse criterion could map to separate input tasks. | Paraphrases can weaken quantities, exclusions or evidence requirements. Compare the proposed mapping with the current verbatim rule on examples that deliberately drop a requirement. Consider preserving exact text for requirements whose wording is material. |
| **4. Make waiting an accountable decision.** | Record who owns the next move, what evidence or decision is missing, which tickets it affects and what event should wake the owner. After a conflict, permit a bounded reassessment with the exact rejection. Preserve the reason and responsible owner if it recurs. | Duplicate or missed events can cause repeat model calls, duplicate effects or indefinite waits. Use durable commitments, current-state reads, idempotent commands and bounded reconnect discovery. A timer alone does not demonstrate progress. |

**Recommended boundary.** Let agents judge meaning, sufficiency, sequencing, dependency impact and readiness to propose work. Keep deterministic enforcement for permissions, hiring and spending authority, approved source identity and revision, stable ticket IDs, valid dependencies, required reviewer independence and authoritative sprint start. A model can judge that a new fact changes an issue. Code should bind that judgment to real evidence and preserve the previous resolution. Evaluate whether fixing the Producer's reconciliation alone is sufficient before broadening platform behavior.

## 5. How to prove or disprove recovery

Use the existing Prism Break business first. It retains both the completed draft and the resolved issue that triggered the conflict. A fresh business can later test first-time startup without those retained records. Neither a package update nor a successful model call proves recovery.

1. **Issue handling.** Replay the same approved source and resolved art issue without new evidence. Verify that history remains intact and Gabriel reaches Story review for unaffected work. In a second case, supply genuinely new, authorized evidence and verify that reopening preserves the old episode.
2. **Draft review.** Inspect the live model request for Victor's exact Story and six briefs. Verify a decision against that proposal. If Gabriel requests revisions, identify the affected criterion, the owner and the change requested.
3. **Publication.** Observe actual Story and task IDs, parent links and dependencies. Replay the handoff and check that no duplicate tickets appear. If the Producer selects partial scope, account for every deferred criterion and task.
4. **Preparation.** Observe current assignments, owner estimates, available capacity and the tasks committed to Sprint 1. Where staffing or capacity is insufficient, record the exact task and next action. The proposed Engineer and QA roles have yet to demonstrate capacity for this run.
5. **End-to-end success.** Observe a passing preflight, an **Active** Sprint 1 with a start timestamp and its execution record. Record first worker claim separately. If publication succeeds but preflight fails, report publication as recovered and the remaining obstacle by name. These proposals cannot guarantee sprint start.

## 6. Questions for the reviewing AI

- Which parts of the diagnosis follow directly from records, and which are interpretations? What evidence would change your conclusions?
- What is the smallest change that lets Gabriel handle the existing resolved issue and reach Victor's draft? Where should responsibility sit among the Producer, shared agent SDK and platform? Compare an agent-side fix with a platform change.
- For the six proposed tasks, which could plausibly depend on art ownership? What additional task or resolution evidence would you need before authorizing partial publication?
- Which decisions should an agent own, which invariants should code enforce, and which of the four proposals would you narrow, reject or defer? How would you detect weakened acceptance criteria or a false claim of completion?
- Provide a prioritized implementation sequence, failure and replay tests, and observable live acceptance criteria for **publication recovery** and **sprint activation** separately. Identify any genuine owner decision needed to proceed.

## Evidence and implementation map

The evidence excerpts above can be evaluated without a signed-in C-Sweet instance. The references below allow a reviewer with local access to reproduce the trace.

- **Run identity:** QFN Games organization `729838c3-7be4-4cc7-bfd4-1c5c3dd847df`, Prism Break project `8d1d7954-1cac-49ff-ab28-230d20220394`, board `e5cac6f6-62e8-46f2-88f1-caaa593a1abb`, Sprint 1 `a172c84b-2f7c-4d29-a4ac-3dde9eeea721`. Other businesses reused the project name.
- **Handoff:** Victor's completed session `a5d95b5b-509e-4734-978d-6e1b2efe49ca`. Gabriel's planning commitment `ff6349bb-f202-43ef-bdae-60d394154519`. The November 16 [first-handoff report](qfn-games-prism-break-2026-11-16.md) and [UTC event ledger](qfn-games-prism-break-2026-11-16.events.json) cover the earlier observation window.
- **Live audit records:** [review request with null draft](https://localhost:7033/api/core/organizations/729838c3-7be4-4cc7-bfd4-1c5c3dd847df/employees/bf5f4137-4d33-4cb0-b4dd-373905c0b8b2/timeline/83139f65-eaa5-ff20-3370-292616482dfe), [recorded same-Story decision](https://localhost:7033/api/core/organizations/729838c3-7be4-4cc7-bfd4-1c5c3dd847df/employees/bf5f4137-4d33-4cb0-b4dd-373905c0b8b2/timeline/9606297d-b074-4349-a052-05edbe229b1f), [6.21.2 issue conflict](https://localhost:7033/api/core/organizations/729838c3-7be4-4cc7-bfd4-1c5c3dd847df/employees/bf5f4137-4d33-4cb0-b4dd-373905c0b8b2/timeline/9d93b3ec-6c60-4f72-a438-103d594f9a49).
- **Producer repository:** `CSweet.Agent.Producer.VideoGame`. Trace `ReviewOwnershipAsync` in `RoadmapOwnership.cs`, `ReconcileOwnershipFindingsAsync` in `OwnershipReconciliation.cs`, `AdvanceProgressivePlanningAsync` and `ValidateProgressiveProposal` in `ProgressivePlanning.cs`, and `ReviewAdaptivePlanAsync` in `AdaptiveSprintReview.cs`. Follow `PublishCanonicalBacklogAsync`, `ReconcileEstimatesAndQaReadinessAsync` and `ReconcileSprintReadinessAsync` in `SpecialistAgent.cs`. See `StoryAdmissionTests.cs`, fixture `prism-first-story-review.json`, and `releases/6.21.2.md`.
- **Technical Director and platform:** `CSweet.Agent.TechnicalDirector.VideoGame` uses `ProgressivePlanning.cs` for technical refinement and task briefs. In `csweet`, inspect [WorkIssueService.ReconcileAsync](../../../src/CSweet.Infrastructure/WorkManagement/WorkIssueService.cs) for the reopening check and [WorkOrchestrationService.PreflightCoreAsync and StartAsync](../../../src/CSweet.Infrastructure/WorkManagement/WorkOrchestrationService.cs) for sprint start.
