# QFN Games and Brickfall startup on November 15

The fresh business is running the updated Producer 6.17.0 and Director 0.12.0. It completed project creation, GDD and production-brief acceptance, then stalled at specialist staffing. Marcus returned the first proposal. Gabriel received the decision but retained its old pending-review wait. The fallback timer eventually produced a technical-only revision, which Marcus also returned because its purpose was truncated and its authority/capabilities were inadequately represented. Both decision events completed successfully without immediate revision continuation. At the final observation, no technical hire or Sprint 1 existed and the planning task again retained a stale awaiting-review wait, now until 7:21 p.m.

## Run identity

| Field | Value |
|---|---|
| Business | QFN Games, `ca5e0700-e97d-4a73-b400-32ce175e8ee9` |
| Project | Brickfall: Neon Cascade, `1294ea9c-7748-499d-a0fa-71d7823d9f6c` |
| Team | Video Game Team, `baa5f7b8-f61a-4d42-bb3c-7ea4fa8bec91` |
| Start anchor | November 15, 6:02:25.493 p.m. PST: CEO membership created |
| Observation window | Approximately 6:54–7:07 p.m. PST; initial and follow-up snapshots below |
| Ledger | [Timestamped interaction data](qfn-games-brickfall-2026-11-15.events.json) |
| Previous run | [Qwen Games and Prism Break](qwen-games-prism-break-2026-11-15.md), a different organization on earlier versions |

All displayed times are **November 15, 2026, America/Los_Angeles (PST)**. UTC timestamps and source references are in the ledger. The four nonempty conversations were read from the CEO and all four agent perspectives, with relevant approval records, personal work and boards. The Producer's complete decision-wake audit window from 6:51:24–6:52:00 p.m. was inspected; there was no further page. Comprehensive inference totals were not collected.

## Verified installed agents

| Agent | Installed version | Installation |
|---|---|---|
| Evelyn Brooks | 2.17.2 | `d07e4f09-157a-4dfc-9f5a-2c1744eed2b7` |
| Marcus Vale | 0.12.0 | `345835fa-758c-4639-8353-d04a4978752c` |
| Naomi Chen | 4.2.2 | `3db81bbe-f18b-4bb2-ad14-5c021e942e6a` |
| Gabriel Reyes | 6.17.0 | `96ce91d3-552e-4756-9d1b-cda251186f27` |
| Victor / technical participant | Not hired | No roster entry |

These are installation records, not assumptions based on local source manifests. The installed SDK version was not separately measured.

## Startup flow and stopping point

```mermaid
sequenceDiagram
    actor CEO
    participant E as Evelyn
    participant M as Marcus
    participant N as Naomi
    participant G as Gabriel
    participant P as Platform
    E->>CEO: Switch SaaS profile to Game Studio?
    CEO->>E: Approve; automatic hiring with broad delegation
    CEO->>E: Focus on Creative and Product
    E->>P: Fulfill Director hire
    P-->>M: Available 6:37
    CEO->>M: Breakout pitch; milestone review
    M->>CEO: Official pitch and initial studio staffing
    CEO->>M: Approve pitch and Creative/Producer roles 6:41
    E->>P: Fulfill approved roles
    P-->>N: Available 6:42
    P-->>G: Available 6:42
    M->>G: Request project proposal
    G-->>M: Proposal submitted
    M->>G: Project created 6:44
    M->>N: Request GDD
    N-->>M: GDD submitted
    M->>G: GDD accepted; refine shared brief
    loop Two production brief drafts
        G->>M: Brief and questions
        M->>N: Exact-draft creative review
        N-->>M: Advisory findings, no creative blockers
        M-->>G: Decisions and scoped follow-ups
    end
    M->>G: Brief accepted 6:50:03
    G->>M: Propose Technical Director and QA 6:50:28
    M-->>G: Return for changes 6:51:24
    P->>G: Staffing decision event 6:51:25
    G->>P: Read current requests
    P-->>G: RevisionRequested
    Note over G,P: Callback succeeds; existing wait remains until 7:05:28
    P->>G: Timer fallback claims task 7:05:51
    G->>M: Technical-only proposal 7:06:08
    M-->>G: Return: clipped purpose, authority, capabilities 7:06:25
    P->>G: Second decision wake succeeds 7:06:29
    Note over G,P: Awaiting-review wait remains until 7:21:08
    Note over E,P: No approved specialist slot to hire
    Note over M,G: No technical participant, project tickets or Sprint 1 yet
```

## Interaction milestones

| Time | Interaction | Observed result |
|---|---|---|
| 6:02:25 | Business creation anchor | New organization, distinct from prior Prism Break |
| 6:20:19–6:21:03 | Evelyn hired → CEO | Recommends switching SaaS profile to Game Studio |
| 6:27:32–6:30:09 | CEO ↔ Evelyn | Switch profile; automatic hiring, broad delegation, prefer first-party; asks business focus |
| 6:36:13–6:37:22 | CEO → Evelyn → hiring | Creative/Product selected; Marcus hired; leadership recommendation fulfilled |
| 6:37:51–6:40:07 | Marcus ↔ CEO | Milestone reviews selected; same Breakout/VFX/chiptune task provided |
| 6:39:51–6:39:58 | CEO ↔ Evelyn | Pauses new proactive leadership areas; does not cancel approved role fulfillment |
| 6:40:27–6:41:38 | Marcus ↔ CEO | Pitch submitted and approved; initial Creative/Producer staffing approved |
| 6:41:40–6:42:31 | Marcus/Evelyn → platform | Empty studio board created; Naomi and Gabriel hired |
| 6:42:32–6:44:18 | Marcus ↔ Gabriel | Project requested, proposed, approved and created |
| 6:42:41–6:42:47 | Naomi ↔ Marcus | GDD readiness greeting; generic authority-boundary reply |
| 6:43:10–6:43:14 | Gabriel ↔ Marcus | Approved foundation requested; documentation task confirmed |
| 6:43:24–6:43:33 | CEO ↔ Marcus | Milestone message reports selected and successfully acknowledged |
| 6:44:21–6:45:53 | Marcus → Naomi → Marcus | GDD requested, submitted and accepted |
| 6:45:54–6:46:51 | Marcus ↔ Gabriel | Production brief requested; first draft and six questions returned |
| 6:46:54–6:47:56 | Marcus → Naomi → Marcus | First creative review passes; refinements are advisory |
| 6:47:51 | Marcus → CEO | Portfolio report: Detailed Design; brief convergence next |
| 6:48:14 | Marcus → Gabriel | Resolves power-ups, failure/session planning, accessibility placement/semantics, lightweight validation and local deployment |
| 6:49:13–6:49:52 | Gabriel → Marcus → Naomi → Marcus | Revised brief ready; review clean except nonblocking session-end advisory |
| 6:50:03–6:50:11 | Marcus ↔ Gabriel | Exact brief and sources accepted; personal planning task created; approved package shared |
| 6:50:28 | Gabriel → Marcus | Technical Director plus QA proposed; personal task waits for review until 7:05:28 |
| 6:51:17 | Gabriel → Marcus | Approved project, delivery setup unconfirmed |
| 6:51:24 | Marcus → Gabriel | Proposal returned; immediate technical help justified, timing/authority/QA independence need correction |
| 6:51:25–6:51:28 | Platform ↔ Gabriel | Decision wake claimed; current returned request read; callback completed without requeue or replacement proposal |
| 6:51:26 | Gabriel → platform | Delivery-plan read denied for missing project grant; callback continues |
| 6:55:06 | Marcus → CEO | Oversight portfolio report; does not identify returned staffing as the next action |
| Initial snapshot | Canonical work/approvals | One returned specialist request; no technical hire, project-linked board, project tickets or sprint; no pending CEO approval |
| 7:05:51 | Timer → Gabriel | Scheduled planning task starts again without human nudge |
| 7:06:08 | Gabriel → Marcus | Second proposal removes QA, requests only Technical Director; purpose truncated again |
| 7:06:25 | Marcus → Gabriel | Returns second proposal for complete purpose, technical escalation/authority and artifact capabilities |
| 7:06:27–7:06:29 | Platform ↔ Gabriel | Second decision wake claimed and completed; task still has pending-review wait until 7:21:08 |
| Final snapshot | Roster / approvals / personal work / sprints | Four agents, two returned requests, no Victor or sprint, no third proposal observed |

## What the staffing proposal actually says

Request `38aebd61-0b58-45e4-81a1-8d01f5f548df` was submitted at 6:50:28 and returned at 6:51:24. It retains the already filled Producer and Creative Director roles and proposes two additions.

| Role | Judgment expressed in rationale/purpose | Submitted structured fields |
|---|---|---|
| Technical Director | Immediate Phaser/Chromium feasibility and decomposition of the vertical slice, effects, mechanics, accessibility and evidence | Timing: “Next planned increment”; reports to Gabriel; required capabilities: `work.execution.run` |
| QA | Bounded independent acceptance after first testable core-loop/vertical-slice deliverables | Timing: “Next planned increment”; reports to Gabriel; required capabilities: `work.execution.run` |

Gabriel's judgment already distinguishes immediate technical work from later QA work. The submitted structure loses that distinction. The Technical Director purpose is also cut off mid-sentence by the source's 700-character construction limit.

Marcus's review agrees that the technical capability gap is real. It requests immediate technical timing, direct feasibility escalation/authority, conditional QA activation and independent reporting, and reassessment of later disciplines after technical decomposition. These are this agent's project-specific review decisions. A standing rule that all technical staff must report to Marcus, or that every game needs another staffing review gate, is not established by the platform rejection or this report.

The platform accepted the proposal submission and the manager's RequestRevision decision. This is **not a JSON validation failure, unavailable hiring candidate, missing budget, missing CEO response or failed technical task**. No technical task has been assigned to Victor because no specialist staffing package has been approved.

## Why the decision wake did not advance work

The event was delivered: audit `3a6ad648-1d4e-4a8c-aac1-1cd38757de59` records Gabriel claiming `com.csweet.management.resource-change.decided.v1` at 6:51:25.455. Audit `17d2cf61-2f3b-4a3a-95e6-08b66bb742bd` records an authorized resource-context read at 6:51:27.930. Its response contains this request in `RevisionRequested` state. The callback completed at 6:51:28.518; it did not revise the proposal or requeue the task. The complete audit window contains no model call or personal-task requeue.

The corresponding source path is:

1. Producer `SpecialistAgent.HandleManagerEventAsync` routes the staffing decision event into an attention review.
2. `HandleManagerAttentionReviewAsync` calls `EnsurePlanningCommitmentAsync` for the accepted handoff.
3. `EnsurePlanningCommitmentAsync` recognizes hiring readiness only after a Technical Director appears. It computes a planning-contract change timestamp only for blocked work; that timestamp describes implementation-contract recovery, not a staffing decision revision.
4. `EnsureCommitmentAsync` preserves an unexpired wait unless a recognized prerequisite changed. The task is Running with a future wait, the technical participant remains absent, and the manager's returned staffing decision is not passed as a relevant dependency change.
5. `EnsureStaffingCorrectionCommitmentsAsync` creates continuation work for explicit pending staffing corrections, not every ordinary RequestRevision. This request has `correctionId: null`, so it supplies no separate revision commitment.

The event therefore wakes the agent without making the **affected work** executable. Preserving deadlines protects against duplicate event storms, but a changed decision on the exact awaited request should be distinguished from an unrelated notification. At the initial snapshot the task still said “awaiting the Director's review” even though that review had finished.

The timer is a fallback, so the initial observation established a missed immediate continuation, not an indefinitely dead task. Its next review was **7:05:28 p.m.** The task was claimed at **7:05:51**, and Gabriel submitted a second proposal at **7:06:08**. The first manager return therefore waited **14m 44s** for a replacement proposal, despite a delivered decision event.

Marcus returned the second proposal at **7:06:25**. The second decision wake was claimed at **7:06:27** and completed at **7:06:29**, but the task stayed at revision 6 with an awaiting-review wait until **7:21:08**. This confirms the stale-wait pattern repeated; it was not just a single delayed notification.

## What the timer exposed and what remains at risk

The fallback demonstrated that `AssessKickoffCoverageAsync` can reassess the first returned proposal on a later execution. Second request `de058abe-c647-45b5-897c-8e9f784a685b` removes QA. Its rationale explicitly includes immediate technical work, direct Director escalation and the authority to block implementation planning for unresolved feasibility conflicts. However, `ProposeCoverageAsync` still supplies fixed timing, reporting and generic capability fields. The role purpose ends at “accessibility t” because of the 700-character construction limit. Marcus explicitly cites the incomplete purpose, missing encoded authority and insufficient artifact-producing capabilities in the second return. The model's prose is more appropriate, but the submitted assignment cannot fully carry it.

After this second return, the current ordinary kickoff path still starts from a special legacy rejection or explicit staffing-correction replacement and then permits one returned-response assessment. It does not generally select the newest ordinary returned request as its next feedback basis. A future timer may therefore replay the existing assessments rather than produce a third changed proposal. This last recurrence is a source-based risk; the 7:21 execution was not observed.

Project-board creation follows qualified technical coverage in `ReconcilePlanningAsync`. Consequently, the staffing package holds up project tickets and sprint planning. The already available Producer/Creative/Director work should be examined for draft activities that can safely continue under assumptions; no new blanket technical-review gate is recommended.

## Why the Overview shows no attention card

The dashboard reports waiting personal work, zero pending attention items and no project incidents. The returned staffing decision belongs to Gabriel and Marcus, not the CEO. It should become actionable agent revision work; creating a mandatory CEO question would move the same coordination defect onto the user. A concise waiting/activity entry should nevertheless describe the current owner and requested revision, rather than displaying an already completed review as pending.

The missing `work.delivery.read.v1` project grant is a separate attention-path finding. Audit `be32512e-4aaf-43ef-baa9-4bf2c25078a8` establishes the denied read, but the callback continued afterward. Do not label it the cause of the staffing wait or grant broad project access as a substitute for completing setup and discovering authorized operations.

## Comparison with the earlier baseline

| Measure | Qwen / Prism Break, earlier versions | QFN / Brickfall, updated agents |
|---|---|---|
| Producer / Director | 6.16.0 / 0.11.0 | 6.17.0 / 0.12.0 |
| User pitch | Breakout, strong VFX/juice and chiptune, desktop web, no budget/deadline cap, no profiling/stress testing | Same task text |
| Accepted scope generated by agents | Ten layouts, three brick types, three original looping tracks | Five layouts, three collectible power-ups, accessibility controls and audio-reactive effects |
| Pitch acceptance → brief acceptance | 10m 08s | 8m 32s |
| Initial Creative/Producer roster → brief acceptance | 9m 09s | 7m 39s |
| Production-brief drafts / creative reviews | Three / three | Two / two |
| Brief acceptance → first specialist proposal | 36s | 24s |
| First specialist staffing review | 24s; returned | 56s; returned |
| Reporting format reply | Queued-context failure | Answer acknowledged successfully |
| Initial specialist staffing judgment | Technical Director and QA | Technical Director now; QA from first testable deliverable, but structured fields do not preserve timing distinction |
| Staffing continuation | Two returns; repeated blocked recovery | Two returns; first timer produces revised proposal, both decision wakes retain stale waits |
| Technical hire / Sprint 1 | Not reached | Not reached at final observation |

The new run's documentation convergence was shorter by observed elapsed time. Scope differs because agents elaborated the same user pitch differently, and model configuration/cost were not measured, so this is not a controlled speed benchmark. Neither run has demonstrated successful technical staffing or Sprint 1. The extra Game Studio profile question and the interval before Evelyn was hired also make total business age unsuitable as a pure planning-time comparison.

## Recommended resolution boundaries

No implementation changes are made in this analysis. The next change should address the interaction contract, not require more manual nudges or force approval of disputed QA scope:

- Bind waiting work to the exact request/decision revision it awaits. On event or reconnect, read current authorized state and turn a returned decision into owned revision work. Deduplicate by consumed decision revision; preserve unrelated waits and in-flight work.
- Represent judgment-bearing work arrangements generically: activation condition/timing, responsibility and independent escalation, bounded versus ongoing work, and required capabilities. Bind identities and catalog roles in code. Preserve the agent's reasoning without truncation. A third-party qualified participant should be able to satisfy the same contract.
- Continue each revision from the newest applicable manager feedback and proposal lineage. Prevent duplicate proposals for unchanged evidence, while allowing materially changed proposals after each independent review.
- Allow separable staffing needs or draft planning to progress when only another part needs revision. Keep exact approval and authorization boundaries; do not silently approve part of a package that was returned as a whole.
- Report the actual pending operation and accountable owner. Test delivered, duplicate, missed and out-of-order events; QA deferred while immediate technical work proceeds; repeated revisions; restart recovery; and the eventual board/Sprint 1 transition.

## Observation history

- **Approximately 6:58 p.m. PST:** Updated agent versions verified. Production brief accepted; first specialist package returned. Decision event delivered and current return read. Planning task remains Running at revision 3 with next review 7:05:28. Zero pending CEO approvals, no technical hire, zero board items and no project-linked board. No code or live work changed; no messages sent to agents.
- **Approximately 7:07 p.m. PST:** Timer continuation observed. Technical-only second proposal submitted 7:06:08 and returned 7:06:25. Return names the truncated purpose, authority relationship and capability mismatch. Second decision wake completed 7:06:29. Planning task Running at revision 6, stale review wait until 7:21:08. Active roster still Evelyn, Marcus, Naomi and Gabriel; sprint list empty. No third proposal observed. No human nudge or implementation/live-state changes made by this investigation.

## Evidence links and source map

Implementation follow-up: [staffing review continuation](../staffing-review-continuation.md) records
the source changes, regression coverage and publication order for recovering this business in place.
This is implementation evidence, not a claim that the live run has recovered; the last observed live
milestone above remains unchanged pending publication and a new observation.

| Subject | Reference |
|---|---|
| Producer task | [Tracked planning work](https://localhost:7125/organizations/ca5e0700-e97d-4a73-b400-32ce175e8ee9/work/tracking/9a119b0d-720b-437c-8015-b41008dc50f8) |
| Staffing conversation | [Marcus and Gabriel](https://localhost:7125/organizations/ca5e0700-e97d-4a73-b400-32ce175e8ee9/communications/07324d72-2d73-4dd5-a0d1-91ef4302d6da) |
| Accepted production brief | [Exact revision](https://localhost:7125/organizations/ca5e0700-e97d-4a73-b400-32ce175e8ee9/documents?artifact=46002106-3094-4db8-bf2c-6c579883d7c1&revision=78c064d5-af53-487a-93f3-0f0e6db6a41e) |
| Current approvals | `GET /api/core/organizations/{org}/approvals` |
| Installed versions | `GET /api/agents/installations` |
| Personal work and wait deadlines | `GET /api/organizations/{org}/work/personal-todos/` |
| Source: decision event publication | Platform `ResourceChangeService.DecideAsync` saves decision and event outbox before audit |
| Source: event → commitment | Producer `SpecialistAgent.HandleManagerEventAsync`, `HandleManagerAttentionReviewAsync`, `EnsurePlanningCommitmentAsync`, `EnsureCommitmentAsync` |
| Source: staffing judgment and packaging | Producer `KickoffCoverage.cs` / `AssessKickoffCoverageAsync`; `AdaptiveDelivery.cs` / `ProposeCoverageAsync` |
| Source: explicit correction continuation | Producer `StaffingCorrections.cs` / `EnsureStaffingCorrectionCommitmentsAsync` |

