# Qwen Games · Prism Break · fresh business, November 17

Organization `ba3a06e1-fec0-451a-8bf7-6ca7f370061f`, project `765fff33-4ecc-47bf-9203-40f7cca4ff45`, board `c84e6f46-d8fe-4b12-8e7c-e2b5293810a5`. Created 07:36:31 UTC (11:36 p.m. PST November 16). Read-only inspection at 16:13 UTC through the owner's signed-in Chrome. Producer 6.21.4, Technical Director 6.13.1. Model `qwen3.8-flash-next`. Times below are UTC.

## State at inspection

Eleven Epics, zero Stories, zero tasks. Production Sprint 1 Planned and empty. Gabriel's planning commitment Blocked since 08:16:44. Marcus's "Review blocked work" Blocked since 08:20. Nothing changed for eight hours.

The roadmap no longer starts with a governance Epic. Epic 1 is "Technical Feasibility Spike and Preliminary Delivery Sequence", and every later Epic depends on it directly or through Epic 2 or 3. Epics 9 to 11 are evidence, audit and review packages.

## Sequence

| Time | Event |
|---|---|
| about 08:13:28 | Gabriel's ownership review request is sent. |
| 08:14:17 | Daniel Kim (software-developer) joins the roster. |
| 08:14:51 | The ownership review returns 15 commitments and one gap: "No approved engineering implementer for first spike", Scoped to Epic index 0. |
| 08:15:37 | Planning review, attempt 1. Request carries `candidates: []`, `provisionalStory: roadmap-epic-1.story-1` and three task briefs. Reply: `select`, `nextCandidate: "roadmap-epic-1.story-1"` (a key, not an index). Its feel summary notes the engineer is now staffed. |
| 08:16:17 | Attempt 2: `select`, `nextCandidate: null`. |
| 08:16:43 | Attempt 3: `select`, `nextCandidate: 0`. Well formed, but there are zero candidates. |
| 08:16:44 | Commitment Blocked: "game planning review: the model reply was not accepted after 3 attempt(s)", reported as a C-Sweet technical issue. |

## Causes

1. **A staffing observation was recorded as a scope-ownership gap.** The ownership review received the roster (`currentResources`) and reported a missing implementer while that hire was completing. The gap was out of date 34 seconds before it was returned.
2. **One hold on Epic 1 held everything.** Every Epic depends on Epic 1, so `OwnershipCandidates` returned no candidates.
3. **The review was asked a question with no valid answer the model would give.** `AdvanceProgressivePlanningAsync` set the draft aside, then called `ReviewAdaptivePlanAsync` with zero candidates and the retained draft attached. The prompt shows a ready draft; `ValidateReview` rejects any selection when the candidate list is empty.
4. **Every rejection carried the same message.** `ValidateReview` had about seventeen rules and one combined sentence. The model changed a different field on each attempt.
5. **The result was classed as a permanent technical block.** `AgentModelJsonException` derives from `InvalidOperationException`, so the progressive-planning catch reported a code defect and the commitment was not retried.

The Producer 6.21.3 change that removed the resource fingerprint from the ownership review key would also have kept this stale verdict from being judged again after the hire. It did not get that far here, because the block happened in the same pass.

## First fixes (released as part of Producer 6.22.0)

| Change | Where |
|---|---|
| The ownership review no longer receives roster or resource state, and is told staffing, capacity and readiness are never ownership gaps. A retained staffing-only obligation is returned as owned by the Epic it serves. Key `producer-roadmap-review.v6`. | `RoadmapOwnership.cs` |
| When no candidate is eligible, gaps are open and no sprint has completed, planning waits on the reconciliation and names the holds, without a model call. | `AdvanceProgressivePlanningAsync` |
| Each review rule throws its own correction, including "no candidate is eligible; use wait or refine" and "nextCandidate is a zero-based index". | `ValidateReview` |
| Planning contract `scoped-planning-recovery.v11` releases the blocked commitment once. | `OwnershipReconciliation.cs` |

Tests: `AFullyHeldRoadmapWaitsOnItsReconciliationInsteadOfAskingForAnImpossibleSelection`, `ARejectedReviewReplyNamesItsOwnCorrection`. 606 pass on Linux against sibling SDK sources. Not verified live.

## Still open

- A rejected model reply during planning is still reported as a code defect and blocks. It should wait and retry within a budget.
- The issue opened for the staffing gap is still Open on the board. After the update it resolves only if the next ownership review returns that obligation as owned.
- Scope: the first Epic is a feasibility spike and three Epics are evidence or review packages. Whether the accepted brief asks for those has not been checked.
