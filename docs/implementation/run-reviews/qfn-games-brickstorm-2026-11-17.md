# QFN Games · Brickstorm · fresh business, November 17

Organization `2c242d16-4b06-4ba8-b984-f2b93819aebf`, project `b14eaa29`. Created 17:39 UTC (9:39 a.m. PST). Read-only inspection at 19:30 UTC through the owner's signed-in Chrome. Agent SDK 5.40.0 and Producer 6.22.0 were published at 9:20 a.m.; Technical Director 6.13.1. Times below are UTC.

## State at inspection

The project board has no Epics, Stories or tasks. Gabriel's planning commitment has been Blocked since 18:03. The roster is Evelyn, Marcus, Naomi, Gabriel and Victor; no engineer or QA was hired. The run stopped one step earlier than the previous two: the Epic roadmap was never produced.

## Sequence

| Time | Event |
|---|---|
| 17:56 | Victor joins. Gabriel requests the Epic roadmap. |
| 17:58:26 | Victor's commitment inventory, attempt 1: 42 commitments (21 Outcome, 21 Constraint), four with empty `sourceIndexes`. |
| 17:59:01 | Attempt 2: three uncited. |
| 17:59:31 | Attempt 3: two uncited. Page fails after three attempts. |
| 18:02 | A second generation pass fails the same way. |
| 18:03 | Victor's session ends Blocked. Gabriel's commitment is Blocked: "Technical Director planning generation or validation failed … No retry is confirmed." |

## Causes

1. **One correction per attempt.** `ValidateCommitments` threw on the first uncited entry and named only that one. The model fixed it and the next uncited entry was reported on the next attempt. The reply was converging (4, 3, 2) and ran out of attempts.
2. **The uncited entries could not be cited.** They restated manager direction about asset fallback and usage rights ("Procedural fallback is allowed…", "Every provided or generated asset must have declared project-use rights…"). That direction is in the conversation but is not one of the 428 numbered accepted sources.
3. **One bad entry failed the whole page**, and the page failure failed the roadmap.
4. **The Producer treated it as permanent.** A blocked Technical Director session was returned as Blocked, outside the retry-later path added in 6.22.0, and the session request is idempotent, so asking again would have returned the same failed session.

The 6.13.1 change that added `kind` to each commitment made the reply longer but is not the failing rule; the source-index rule predates it. The 6.22.0 retry-later path was not reached in this run.

## Changes

**Technical Director 6.13.2**
- All uncited commitments are reported together, with the instruction to cite or remove them.
- On the third attempt, entries that still cite no accepted source are omitted and recorded on the timeline; the inventory continues with the cited ones. An inventory with nothing citable still fails.
- The prompt states that manager directions and resource notes are not commitments.

**Producer 6.22.1**
- A Technical Director generation failure returns `RetryLater`, and each retry requests a fresh generation under a new request key (`ProducerPlanningCycleState.GenerationRetries`).
- Planning contract `scoped-planning-recovery.v12` releases the blocked commitment once.

Tests: Technical Director 255 pass, Producer 609 pass, both on Linux against sibling sources. The Producer change sits in the outer planning shell, which the scripted journeys do not cover; it is tested only at the session-request level. Not verified live.

## Observation

The accepted brief was split into 428 numbered sources and produced 42 commitments, half of them constraints. That is a large contract for a Breakout clone and is upstream of everything inspected here.
