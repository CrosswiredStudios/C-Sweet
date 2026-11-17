# QFN Games / Shardfall: ticket-planning stall on November 16

The current QFN Games business reached ticket planning, then stopped on **two deterministic integration defects**: the broker rejects an estimate provenance field emitted by the Producer, and the Producer reuses an advice-comment idempotency key after its target changes from an Epic to a newly published Story. A separate toolchain feasibility review is blocked over a missing Windows environment profile. Manager recovery did not repair these conditions and presents generic execution failures instead of the actionable errors.

This is a **new organization**, distinct from both November 15 reports. The earlier Brickfall staffing stall does not explain this business: Victor was hired and a project board and draft sprint exist here.

## Identity and observation boundary

| Field | Observed value |
|---|---|
| Organization | QFN Games, `40096d7b-579e-4481-9da9-2e7e1e4ef4cc` |
| Project | Shardfall, `b434f434-0df4-46a0-8d78-3293d4523f52` |
| Team | `a858ae15-5d3f-48b7-a566-dae8c0d97a65` |
| Organization created | November 15, 10:40:24 p.m. PST (`2026-11-16T06:40:24.099372Z`) |
| Capture | November 16, approximately 8:49–8:52 a.m. PST |
| Final activity snapshot | `2026-11-16T16:52:03.4502673Z` |
| Board | `5d0242b5-825e-4c65-9072-080da45491df` |
| Ledger | [Timestamped evidence](qfn-games-shardfall-2026-11-16.events.json) |

Read-only API inspection through the owner's signed-in Chrome covered roster, installations, personal work, activity, board/items/sprints, relevant Producer/Director/Victor collaborations, the existing Epic advice comment and exact failed capability/work events. The Producer's **Failed-outcome timeline from 07:30–08:40 UTC** was read to its end (`nextCursor: null`). This is not an exhaustive export of all agent activity, model calls or costs. No agent messages, retries, permissions or runtime payloads were changed.

## Actual installed versions and achieved work

| Agent | Version |
|---|---|
| Evelyn Brooks | 2.17.2 |
| Marcus Vale | 0.13.0 |
| Naomi Chen | 4.2.2 |
| Gabriel Reyes | 6.18.0 |
| Victor Lin | 6.10.0 |

The project was approved at **11:23:33 p.m. November 15**. The exact production brief was accepted at **11:30:44 p.m.** Victor joined at **11:32:45 p.m.** The current board contains **13 Epics, two Stories and one Task**, all in Backlog. Production Sprint 1 exists in **Planned** state with one item, zero planned points and no `startedAt`. No active sprint or completed board work was observed.

## Confirmed failure 1: valid estimate DTO rejected by stale broker schema

At **12:19:07 a.m. November 16**, Victor completed estimate/capacity session `ff4bb0d2-19f9-4f32-94f1-f8218f8825fb`. Gabriel then tried to record **8 points**, confidence **0.7**, for Task `ea08b8f6-fa1e-4304-9f22-0137a9b07ab8`, “Publish Planning-Only Feasibility and Decomposition Record.”

The platform rejected it at **12:19:20** and again **12:19:22**:

> JSON Schema validation failed: $.provenance.planningRevision is not allowed.

Audit `412ae2c7-5a45-4c10-8f5c-d902041fb208` retains the exact submitted request, including `provenance.planningRevision: 1`. The terminal work event is classified `platform.capability.validation_failed`, `retryable=false`, diagnostic `8ed93479-bf73-499f-960c-f80fd146c008`.

Source explains the mismatch:

- Contracts `WorkEstimateProvenance` in `CSweet.WorkManagement.Contracts/WorkManagementContracts.cs` defines optional `PlanningRevision` and `AssessmentStateKey`.
- Producer `SpecialistAgent` adds `PlanningRevision` when constructing `EstimateWorkItemRequest.Provenance` in the sprint-estimation path. This field is emitted by code; it is not an invented model field.
- Platform `McpToolCatalog.InputFor`, `WorkItemActions.Estimate` arm, uses a handwritten provenance schema with `additionalProperties: false` but omits both fields.

Consequently, the estimate cannot cross the broker even though the shared DTO and Producer agree on the field. The Task's estimate remains null. This directly blocks “Estimate draft scope and reconcile sprint staffing,” personal task `1b6fc6b4-548d-4633-9f87-88d11dfcda52`.

## Confirmed failure 2: retained advice changes target but retains its command key

Victor's scoped advice arrived at **11:38:18 p.m. November 15**. It correctly distinguished Technical Director planning from Engineer implementation: the initial candidate mixed feasibility with dependency locking, scaffold and Chromium startup work despite no Engineer on the roster. The agents refined it into a planning-only increment rather than pretending Victor supplied implementation capacity.

At **11:42:12 p.m.**, the advice was attached to the existing first Epic, comment `1151d05a-b07b-4562-8696-9496307b7291`. At **12:18:00 a.m. November 16**, the more detailed Story and Task were published. Subsequent reconciliations attempted to attach the retained advice to that Story with the same command key:

`review-action:6d9652aab551101c875818f54a08b2f933b6867c9e059e60281524f8384a048a:0:advice`

The platform rejected this at **12:19:13**, **12:19:17** and **12:21:20**:

> The idempotency key was already used for a different work item comment.

Audit `4bff8578-3e31-402c-ada2-1ccf2042d027` shows the attempted Story target `b3287296-d54e-4e44-8eae-423d63225f3e`. The work failure is `platform.capability.conflict`, `retryable=false`, diagnostic `0f122782-7a07-4c57-8f1b-1563c6565f7e`.

Producer `AdaptiveSprintReview.ReadDesignAdviceAsync` resolves an exact Story first, otherwise its parent Epic, then calls `CommentAsync` using only `pending.ArtifactKey + ":advice"`. The same evidence therefore targets the Epic before the Story exists and the Story afterward. Broker `WorkManagementCapabilityHandler.CommentItemAsync` replays the original command and correctly rejects a different `WorkItemId`. The Producer's command identity does not account for its changing destination.

This stops “Reconcile scope, technical backlog and delivery staffing,” personal task `1111e040-661b-44cf-ac4f-9412f4c4532d`, even though the advice itself is already retained. Repeating the same operation cannot resolve the conflict.

## Separate feasibility blocker and remaining capacity uncertainty

At **11:49:34 p.m. November 15**, Victor returned `Feasible: false` for `phaser.web-2d.v1` in session `561f86d6-b742-404d-a9a6-4b2d13a496c2`. His findings say:

- The `web` target matches the accepted Chromium-only scope and the game is implementable in Phaser.
- The supplied Linux profile provides compatible Node/npm/Chromium/Playwright tools.
- The recipe lists **both** `office.windows-x64.node-24.v1` and `office.linux-x64.node-24.v1`; the supplied catalog lacks Windows. He treats that as a blocking recipe requirement pending remediation or an authorized amendment/waiver.

The NodeTypeScript plugin's `toolchains/node-typescript.v1.json` does list those two profiles. That establishes the declaration and the agent's interpretation; it does **not** establish that a Chromium web game inherently needs both operating systems, or that installing Windows capacity is the correct fix. Review the recipe/profile selection semantics and supplied catalog before choosing remediation. The Director recorded the review as blocked and asked the CEO to choose remediation, a target change or a pause.

The planning-only increment is explicitly separable from this execution-environment issue. It advanced as far as Victor's completed estimate, then hit the broker defect above.

Victor also reported **AvailableSprintCapacity: 0** because no confirmed calendar allocation was supplied. No Engineer is hired. These are further readiness questions after the integration defects are repaired; a successful estimate write alone would not prove the sprint can start. Whether zero capacity reflects actual unavailability or overly conservative assessment was not established.

## Why recovery did not unstick it

Both Producer tasks remain Blocked. The Director's manager-recovery tasks also remain Blocked. One says the reviewed retry left the source blocked; the other asks for owner-assessment/capability validation because current evidence leaves eligibility and resumability unknown. The activity surface reduces both concrete platform failures to “execution stopped unexpectedly.” It does not identify the stale estimate schema or moving comment target.

The current Director runtime excerpt also records a declined retained contribution: Gabriel says a generic request merely restates the toolchain issue without a new concrete, source-bound deliverable. This is an additional recovery failure, not evidence that the original estimate or comment errors are model mistakes.

At the final snapshot, the two Producer tasks had been blocked for approximately **8 hours 31 minutes** and **8 hours 33 minutes**, respectively. Successful attention callbacks and periodic feasibility waits did not demonstrate a resumed planning transition.

## Repair boundaries

No implementation fix or deployment is performed in this review. The concrete follow-up is:

1. Align the broker estimate schema with the public DTO, preferably by deriving it from the wire contract. Verify a real Producer-shaped request containing `PlanningRevision` crosses the broker; also cover direct owner assessments using `AssessmentStateKey`.
2. Make advice-comment effects stable across Epic-to-Story publication. Either retain a stable destination or give distinct destination-bound effects distinct keys, preserving historical advice and retry deduplication.
3. Retain actionable error details in recovery and re-evaluate the existing blocked commitments after changed code/schema evidence. Do not erase command history, force estimates or start the sprint as a diagnostic shortcut.
4. Resolve the recipe/profile interpretation through the proper technical authority. Reconcile actual sprint allocation and future Engineer capacity independently.

## Evidence and source map

| Evidence | Reference |
|---|---|
| Current board and project | [Shardfall work](https://localhost:7125/organizations/40096d7b-579e-4481-9da9-2e7e1e4ef4cc/projects/b434f434-0df4-46a0-8d78-3293d4523f52?tab=work) |
| Estimate task | [Tracked work](https://localhost:7125/organizations/40096d7b-579e-4481-9da9-2e7e1e4ef4cc/work/tracking/1b6fc6b4-548d-4633-9f87-88d11dfcda52) |
| Reconciliation task | [Tracked work](https://localhost:7125/organizations/40096d7b-579e-4481-9da9-2e7e1e4ef4cc/work/tracking/1111e040-661b-44cf-ac4f-9412f4c4532d) |
| Estimate rejection | Employee timeline event `412ae2c7-5a45-4c10-8f5c-d902041fb208` |
| Advice conflict | Employee timeline event `4bff8578-3e31-402c-ada2-1ccf2042d027` |
| Estimate collaboration | Chat `f9d23e76-ccf1-4da5-9708-ad50ef6427fb`, session `ff4bb0d2-19f9-4f32-94f1-f8218f8825fb` |
| Original advice | Same chat, session `4d49ad74-199e-474b-82e9-f815171b8de1` |
| Feasibility collaboration | Chat `a4c0b16d-ec76-4626-b7cb-61f3782a81b5`, session `561f86d6-b742-404d-a9a6-4b2d13a496c2` |
| Estimate schema | Platform `CSweet.AgentHost/Broker/McpToolCatalog.InputFor`, `WorkItemActions.Estimate` |
| Estimate construction | Producer `SpecialistAgent` sprint-estimation path; shared `WorkEstimateProvenance` |
| Advice effect | Producer `AdaptiveSprintReview.ReadDesignAdviceAsync`; broker `WorkManagementCapabilityHandler.CommentItemAsync` |
| Recipe declaration | NodeTypeScript plugin `toolchains/node-typescript.v1.json`, recipe `phaser.web-2d.v1` |

Validation: documentation/evidence review only. No implementation changes or tests. Recovery and an active sprint have not been observed.

## November 16 implementation and live recovery observation

This section follows the earlier read-only diagnosis. The owner subsequently authorized implementation
and recovery verification. See [inferred requirement resolution](../inferred-requirement-resolution.md)
for shared behavior, regression coverage and source commits.

The local platform was rebuilt and restarted using the existing persistent database. Published SDK
**5.36.0**, Producer **6.19.0**, Director **0.14.0** and Technical Director **6.11.0** passed their release
workflows. The three agents were updated through **Settings → Installed agents**, retaining the same
installation IDs and advancing their normal installation revision to 2. No installed payload,
approved package record, business scope or historical command was patched.

The two retained Producer tasks were still blocked after installation. Normal owner task controls
requeued them around **10:56 a.m. PST**. This is operator-assisted recovery, not proof that a release
alone automatically wakes every blocked task.

Confirmed subsequent effects:

- At **10:56:41**, Story `b3287296-d54e-4e44-8eae-423d63225f3e` received contribution-reference comment
  `6ed09364-c589-421e-a710-165f974c684d`. It points to Victor's original collaboration and digest
  `9E51D60705B1A450004C64496A090A936493B995DF04253514E38232BC529568`.
  The original Epic advice and session remain intact; the Story no longer reuses their comment command.
- At **10:57:46**, Task `ea08b8f6-fa1e-4304-9f22-0137a9b07ab8` advanced to revision 3 with **8 estimate
  points**, confidence **0.7**, `planningRevision: 1`, and the original estimate session/turn digest.
  This is an accepted platform write, not a model promise or a forced replacement estimate.
- Both original Producer tasks moved from **Blocked** to **Running with explicit waits**. Estimation
  reports that no dependency-consistent estimated work fits current capacity. Reconciliation waits
  on current uncertainty. **Production Sprint 1 remains Planned**; zero reported capacity and absent
  Engineer staffing must still be resolved through actual owned planning/resource work.

The first feasibility retry exposed an additional integration defect: the platform correctly resumed
the initiating Director, but his same-recipe handler recorded the old negative evidence again instead
of sending fresh source/catalog evidence to the specialist. At **10:59:48** the session returned to
Blocked without a new Victor turn. Director **0.14.1** corrects that routing; its 453 tests include
resumed same-recipe reviews returning either feasible or unresolved evidence. Publication, installation
and the ensuing feasibility result follow.

Director **0.14.1** published successfully in [release workflow 43935085664](https://github.com/CrosswiredStudios/CSweet.Agent.Director.VideoGame/actions/runs/43935085664).
Installed Agents applied it at **11:04:30 a.m. PST**, retaining Marcus's installation ID and advancing
its revision to 3. A normal board Retry at **11:05:18** resumed the same session. This verification
includes an operator nudge; automatic changed-basis discovery alone was not established.

At **11:06:34**, Marcus sent a fresh request with the exact accepted pitch and current catalog to
Victor, turn `52dc510d-b46d-411e-9044-32646289fe73`. At **11:07:46**, Victor returned
**Feasible: true**, turn `d7e1843c-0928-446d-bbf2-507073f52ed9`. His inference explicitly states
that the certified Linux environment is an eligible build host and Windows is an alternative profile;
the player target remains Chromium web. He treats the exact Phaser version as later dependency
selection work, and distinguishes design feasibility from measured performance.

At **11:08:17**, Marcus recorded that fresh evidence and the retained collaboration became
**Completed**, revision **11**. At **11:08:22**, certified-provider selection decision
`8771186e-3ac5-44e3-a4ca-e1124b183fce` became **Decided**, revision **2**, selecting the existing
provider `ed25b77b-c7b8-4eab-813e-a87042dceec5`. The original fixed-menu Windows blocker
`68d13eb4-7b34-4c99-aa91-c38176625f67` and the intervening inference-authored requirement decision
`1ce4bef1-2c8a-45d4-b53f-bd7dcc945c48` are both **Superseded**, with their history preserved.
No Windows capacity was added and no accepted target or verification requirement was waived.

The intervening inference-authored card demonstrates dynamic questions, options, consequences and
follow-through in the live run. Its diagnosis still reflected the retained erroneous finding; the
fresh specialist review supplied the correction and made that unanswered card obsolete. No owner
answer to the new card was submitted, so actual live follow-through after a chosen option is not
claimed. That path is covered by SDK regression tests.

**Final observed boundary, 11:08:47 a.m. PST:** all three original failure paths have successful
retained-run continuations. Sprint 1 is still **Planned**, not Active; the reported capacity of zero
and future Engineer coverage remain separate planning work. These changes prove recovery past the
three investigated failures, not a completed planning phase, automatic recovery without intervention,
or a finished game.

## Follow-up: zero-capacity cause and repair

The original estimate session resolves the earlier uncertainty. Victor's capacity assumption says no confirmed calendar availability or sprint capacity was supplied, so he treated confirmed capacity as zero pending explicit allocation. His estimate was eight points with no listed blockers. This establishes a missing-evidence inference, not an observed calendar conflict or proof of no availability.

Two implementation choices kept that zero alive: `WorkPlanningJudgments.EstimateAsync` cached by the unchanged request alone, and Producer request identity did not include current availability or the assessment contract. An unchanged completed collaboration therefore continued supplying the same zero. The SDK also lacked calendar observations and did not restrict the personal-board directory to the assessed employee. Producer's empty scope selection produced an unowned periodic wait rather than focused resolution.

SDK **5.37.0**, Producer **6.20.0** and Technical Director **6.12.0** implement [inferred owner capacity and recovery](../inferred-work-capacity.md). Unknown, actual zero and positive offers remain distinct. Evidence is owner-scoped, cached judgment is bound to its current basis, and changed availability fences sprint preparation and readiness. Real shortfalls use inference-authored native decisions attached to the owning work. Capacity is never copied from demand or assigned by this repair.

The SDK [publish workflow](https://github.com/CrosswiredStudios/CSweet.Agent.Sdk/actions/runs/43942363991), Producer [release workflow](https://github.com/CrosswiredStudios/CSweet.Agent.Producer.VideoGame/actions/runs/43942744562) and Technical Director [release workflow](https://github.com/CrosswiredStudios/CSweet.Agent.TechnicalDirector.VideoGame/actions/runs/43942794417) succeeded. Source commits are SDK `9af5a73c44f614404c4638d755a358b22772ef28`, Producer `09ac5cfe071b7a380a5cf67adc99105c8b8981bd` and Technical Director `f142b793d8129843d506278573e4618d6e002946`.

Installed Agents applied Technical Director 6.12.0 at **11:50:36 a.m. PST** and Producer 6.20.0 at **11:50:57**, retaining both installation IDs and advancing both to revision 3. Both builds succeeded from published release bundles. The local platform was rebuilt and restarted with shared capacity guidance. Its normal restart recovery returned the two retained Producer planning tasks to Ready; no task status, capacity, sprint status, decision answer or approved package record was directly rewritten.

At **11:53:40**, Gabriel opened fresh estimate session `f77cd0e2-eee6-45f7-a52d-c4e2f4f0c109` for the same board, planning digest and task, with new request fingerprint `1d216281e7c348966429215ac2158339c441bccf3e7d29cec95fd4c995f95c02`. At **11:54:36**, Victor independently returned an eight-point estimate, medium confidence and **OfferedPoints: 8**, with no blockers. His rationale says the owner has capability for the planning-only record, no material competing work is shown, and uncertainty can be retained in the artifact. He explicitly limits the offer to this increment and distinguishes it from approval, allocation or an execution grant.

The response carries `AssessmentContract: owner-capacity-offer.v2`, state key `work-estimation:v2:563ca44fbfbebd70dd95f47ff66ab1e650ab4cf26d39d47696ab1bac72a381b5` and availability basis `work-availability:v1:bb544c76a57633b1c225b5adff40cd7ee03dd10478b71e4c745421e71c83867f`. The session completed at **11:54:39**. Its completion woke the retained estimation task back to Ready at **11:54:40**, without an operator retry or waiting for its timer.

At **11:56:12 a.m. PST** (`2026-11-16T19:56:12.23038Z`), the existing **Production Sprint 1 became Active**, revision **3**, with **eight planned points and capacityPoints 8**. Its original sprint ID, creation time, task and accepted planning digest are retained. The normal Producer preparation/readiness/start path made this transition; the operator did not allocate capacity, change scope, answer a decision or force the start.

At **11:57:00**, activity showed Victor executing the original planning-only task and waiting for its model response. The board at **11:57:33** showed **Production Sprint 1 · Active**, the eight-point task in **In Progress**, and Victor assigned. This verifies recovery into Sprint 1 and actual task execution, not completion of the artifact or game. No claim is made about later delivery, future Engineer coverage, or live follow-through on a capacity decision: this run's fresh positive offer did not require one. The earlier platform restart remains part of this operator-assisted deployment verification.

Validation for this follow-up: SDK 946 tests, two sample tests, seven generated-template tests and self-test; Producer 593 tests and self-test; Technical Director 253 tests and self-test. Both agents were verified against packed SDK/contracts with sibling references disabled. The platform's model, decision-wake and sprint-service checks passed 50 tests with four existing skips, and its Debug AppHost build succeeded. Final package IDs/versions and hashes are recorded locally in `artifacts/qfn-capacity-package-verification.json`; the live estimate and board screenshot are retained as `artifacts/qfn-capacity-assessment.json` and `artifacts/qfn-capacity-sprint1.jpg`.
