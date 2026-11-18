# Planning flow refactor: retry-later and advisory ownership

November 17, 2026. Code is changed locally in `CSweet.Agent.Sdk` (5.40.0) and `CSweet.Agent.Producer.VideoGame` (6.22.0). Nothing is committed, published or verified live.

## Why

Five fresh businesses stalled between the Epic roadmap and the first published Story in three days. Each had a different trigger and the same shape: a side check or a recoverable failure became a permanent block. See the run reviews for [QFN Prism Break](run-reviews/prism-break-planning-fixes-2026-11-16.md) and [Qwen Prism Break](run-reviews/qwen-games-prism-break-2026-11-17.md).

## What changed

### 1. Retry later (Agent SDK 5.40.0)

A personal to-do attempt now has four outcomes.

| Outcome | Use when | Effect |
|---|---|---|
| `Completed` | The work is done. | Card completes. |
| `WaitingUntil` | Someone else owns the next step. | Wakes on their event, with a timer as fallback. |
| `RetryLater(failure)` | The failure may clear by itself: rejected model reply, platform conflict, facts changed mid-attempt. | Retries after 1, 2, 5, 15, 30 minutes. Blocks with the exact failure when five retries of one failure, or ten of any kind in six hours, are spent. |
| `Blocked` | A named person or authority must act. | Card blocks for its manager. |

- `PersonalTodoRetry.ResolveAsync` keeps the budget in operating state under `personal-todo-retry:v1:<item id>`. Failures are matched by a signature that ignores identifiers and numbers.
- `AgentRuntimeWorker` sends an unhandled handler exception through the same budget. Before, it released the claim and rethrew, and the platform later blocked the card as "execution stopped unexpectedly". Cancellation and resource handoff are unchanged. If the budget cannot be recorded, the old behavior applies.
- This is available to every agent that references 5.40.0. Only the Producer has been moved to it.

### 2. Advisory ownership review (Producer 6.22.0)

- `BoundOwnershipGap.HoldsScope` is true only for a gap with conflict evidence (two binding constraints or decisions that cannot both hold) or a recorded hold whose management decision is still pending. `OwnershipHolds`, candidate selection, draft set-aside and publication all use it.
- An ordinary gap is still opened as a board issue and commissioned to the Technical Director once. It holds nothing.
- Final acceptance is unchanged: `AdvanceProgressivePlanningAsync` will not report completion while any gap is open, and `ReconcileFinalProductAsync` rejects a review with gaps.
- A failed ownership review no longer blocks. Planning continues with recorded holds, and completion returns `RetryLater` until a review succeeds.
- The review prompt no longer includes roster or resources and states that staffing, capacity and readiness are not ownership gaps.

### 3. Planning failures (Producer 6.22.0)

- `AgentModelJsonException` in progressive planning returns `ProgressiveAdvance.Retry`, which becomes `PersonalTodoResult.RetryLater`. Other `InvalidOperationException`s there are still reported as blocks, because they are validation failures that a retry will not change.
- `ValidateReview` returns one specific correction per rule.
- When a binding conflict holds every candidate and no sprint has completed, planning waits without a model call.

### 4. Scripted journeys

`PlanningScenarioTests.cs` drives the real Producer from an accepted roadmap to a published Story with a scripted model and an issue registry that refuses what the platform refuses.

| Journey | Replays |
|---|---|
| Bad replies, a hire and an open gap | November 17: candidate named by key, then omitted; resource change during review; a gap on the first Epic. Ends with one Story and five tasks, the gap still open and commissioned once. |
| A model that cannot answer | Three rejected replies produce a retry, not a block; the next pass selects the Story. |
| A binding conflict | Only the conflicting Epic is withheld from the candidates; independent work is selected. |

Earlier unit tests cover the resolved-issue replay and the crash before the first Story. The journeys stop at publication.

## Tests

Producer: 609 pass. SDK: 959 of 962; the three failures need a git checkout and are unrelated. Run on Linux against sibling SDK and contracts sources. The Producer project now references the `CSweet.Agent.SDK` 5.40.0 package when sibling sources are absent.

## Release order

1. Publish `CSweet.Agent.SDK` 5.40.0.
2. Build and publish Producer 6.22.0 against it, then update in Installed Agents.
3. Technical Director 6.13.1 is already published and unchanged. It gets retry-later only when it moves to SDK 5.40.0.

## Not done

### Splitting `ReconcilePlanningAsync` into steps

Not started. The method is about 330 lines and also carries role repair, scope amendment and the non-adaptive designer path. Splitting it changes how the planning commitment is correlated, woken and recovered, and the journeys do not yet cover its outer shell (package and session setup, estimates commitment). Order to do it safely:

1. Extend the journeys to enter through `HandlePersonalTodoAsync` for the planning commitment, with package, session and decision capabilities scripted.
2. Extract, without behavior change, four methods that each read current state and return one outcome: prepare inputs, track ownership, advance planning, publish and queue estimates.
3. Give "advance planning" and "publish and queue estimates" their own commitments, so a failure in one retries alone.
4. Remove the role-repair and amendment branches from the shared path into their own commitments.

### Other gaps

- **Legal options by name.** The review still takes `nextCandidate` as an index. Candidates should be offered and returned by key.
- **Other Blocked returns.** The Producer has about sixty. Only the planning-review and ownership-review failures were reclassified. Estimates, sprint readiness and delivery need the same pass.
- **Other agents.** Technical Director, QA, Engineer and the Directors still pin an earlier SDK.
- **After publication.** Staffing role match, estimates, capacity, preflight and execution review are outside this change. Shardfall's review loop is untouched.
- **Scope.** Whether the accepted brief asks for the evidence and review Epics has not been checked.

## Sweep of Blocked returns, accepted brief to sprint start (Producer 6.22.1, Technical Director 6.13.2)

Added after the [Brickstorm run](run-reviews/qfn-games-brickstorm-2026-11-17.md), where a failure outside the two rerouted paths blocked planning. Every `PersonalTodoResult.Blocked` on the path was read and classified. The Technical Director has none; its failures end a coordination session, which the Producer now retries.

### Rerouted to retry-later

| Where | Condition | Retry behavior |
|---|---|---|
| `ReconcilePlanningAsync` | Technical Director session blocked on generation | Fresh generation under a new request key (`GenerationRetries`) |
| `ReconcilePlanningAsync` | Completed sessions lack current, correlated artifacts | Fresh generation |
| `ReconcilePlanningAsync` | Accepted handoff or roster unreadable | Read again |
| `AdvanceProgressivePlanningAsync` | Story refinement Failed or Blocked | Draft set aside; same Story requested again under a new key with the reason (`StoryRetries`, `StoryRetryReason`) |
| `AdvanceProgressivePlanningAsync` | Returned Story fails `ValidateProgressiveProposal` | Same |
| `AdvanceProgressivePlanningAsync` | Model reply not accepted | Read current facts and ask again (6.22.0) |
| `ReconcileEstimatesAndQaReadinessAsync` | Estimate session Blocked or Failed | New request key. Previously reported as pending and polled forever. |
| `ReconcileEstimatesAndQaReadinessAsync` | Estimate proposal stale or incomplete | New request key |
| `ReconcileEstimatesAndQaReadinessAsync` | Roster unavailable; planning evidence changed; sprint creation returned a conflicting sprint | Read again |
| `ReconcileWorkflowRecoveryAsync` | Approved team unavailable | Read again |

`RegenerationAttempts.cs` holds the per-request failure count that makes a retry ask under a new key.

### Left as Blocked

| Condition | Why |
|---|---|
| Commitment missing its workstream, team, board or fingerprint | Nothing to retry against |
| Planning sessions in a terminal conflict that is not a generation failure; a cancelled refinement | Needs the Video Game Director |
| Unresolved authority questions (`DescribePlanningBlocker`) | Waits on recorded decisions and is released when they change |
| Kickoff staffing proposal returned or rejected | A manager's decision |
| `WorkPlanningReadiness.Validate` structural issues in selected tickets | Needs a planning correction |
| Profile has no Ready column; installed Technical Director lacks roadmap support | Configuration |
| Role repair and scope amendment paths | Not reviewed in this sweep |
| QA readiness blocked or not approved | Non-adaptive projects only |
| Other `InvalidOperationException` in progressive planning | Validation of already accepted scope; a retry will not change it |

### Journeys added

- `PlanningCommitmentJourneyTests`: enters through `HandlePersonalTodoAsync` for the planning commitment. A failed roadmap generation returns retry-later with the specialist's diagnostic; the next pass requests a fresh generation under a different key and does not duplicate the draft sprint.
- `Journey_a_failed_story_refinement_is_asked_again_with_its_reason`.

The outer-shell journey stops when the fresh session is requested. It does not yet carry a completed roadmap through ownership review to publication from that entry point, and nothing scripted covers estimates, capacity or sprint start. The estimate reroutes are covered only by the existing suite still passing.

### Technical Director 6.13.2

- Uncited commitments are reported together and omitted on the last attempt.
- Constraint-only Epics are reported together.
- `ReadPlanningPageAsync` repeats earlier corrections with each new one.
