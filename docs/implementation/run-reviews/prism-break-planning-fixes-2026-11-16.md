# Prism Break planning stall: fixes and remaining design work

November 16, 2026. Follows the [independent review](prism-break-planning-independent-review.md). Code is changed locally in the Producer and Technical Director repositories. Nothing is published, committed or verified live yet.

## What caused the stall

1. **The Producer asked the platform to reopen a resolved issue.** The ownership review is a model judgment. It was judged again and reported final art ownership as a gap after the issue had been resolved against the same approved brief. `ReconcileOwnershipFindingsAsync` turned every reported gap into an `Open` update and never supplied reopening evidence, so `WorkIssueService.ReconcileAsync` refused it. The call sat outside any error handling and ran before Story admission, so one refused update blocked the whole planning commitment.
2. **The verdict was re-run for reasons unrelated to ownership.** The review's cache key included the whole resource fingerprint. A toolchain moving from Certifying to Ready is the likely trigger in this run. This is an inference from the timeline, not a confirmed cause.
3. **The roadmap made governance the only first candidate.** `BindRoadmap` required every approved commitment, including exclusions and non-goals, to have an owning Epic. A guardrails Epic is the natural place to put them, and other Epics then depended on it. Gabriel had one eligible candidate. This mechanism is read from the code; the recorded roadmap itself was not inspected.

## Changes

### Producer 6.21.3

| Change | Where |
|---|---|
| A gap whose latest issue is Resolved or Superseded on the same approved source is carried as owned. It holds no scope and requests no reopening. A different approved source is a new basis. | `CarryRecordedResolutions` in `OwnershipReconciliation.cs` |
| Follow-up work (manager questions, commissions) derives from the current open issue record instead of assuming every reported gap has one. | `ReconcileOwnershipFindingsAsync` |
| A failed issue update is reported on the timeline and retried next pass. Reviewed gaps and earlier holds still restrict their scope. Story review and publication continue. | `ReconcilePlanningAsync` in `SpecialistAgent.cs` |
| The ownership review key no longer includes the resource fingerprint (`producer-roadmap-review.v5`). | `RoadmapOwnership.cs` |
| A settled select decision on a returned draft is stored under a key bound to the draft, approved package, outline, candidates, holds on that Story and material facts (direction, findings, acceptance, QA, advice). Board item movement, builds, execution state, roster revision and resource readiness do not reopen it. A wait is never stored this way. | `ReviewAdaptivePlanAsync` in `AdaptiveSprintReview.cs` |
| When the next step is the Producer's own, the commitment resumes after one minute instead of the 15-minute fallback. | `ProgressiveAdvance.OwnNextStep` |
| Planning contract revision is `scoped-planning-recovery.v9`, which releases a Blocked planning commitment once after the update. | `OwnershipReconciliation.cs` |

### Technical Director 6.13.1

| Change | Where |
|---|---|
| The commitment inventory marks each entry Outcome or Constraint. Retained inventories without a kind are treated as outcomes. | `ApprovedCommitment.Kind` in `AdaptiveRoadmap.cs` |
| An Epic that owns only Constraint commitments is rejected back to the model with the correction to attach them to the outcome Epics they restrict. | `BindRoadmap` |
| The roadmap prompt states that constraints are not work and that scope or authority agreement is never a prerequisite. | `ReadRoadmapAsync` |

Existing projects keep their accepted roadmap. Prism Break will still offer the governance Story first.

## Tests

Producer: 603 pass, including `AResolvedObligationStaysResolvedWhenALaterReviewReportsTheSameGapWithoutNewEvidence` and a resource change after admission in `First_story_review_uses_returned_briefs_and_recovers_retained_refinement`. The test issue registry now refuses an unsupported reopening the way the platform does. Technical Director: 254 pass, including `Constraints_attach_to_outcome_epics_and_cannot_form_a_governance_epic`. Both self-tests pass. These ran on Linux against the sibling SDK and contracts sources, not the pinned packages.

## Live verification

1. **Prism Break (existing business).** Publish and install Producer 6.21.3. Expect Gabriel's Blocked planning commitment to be released, a timeline entry `ownershipReview: recorded resolutions retained` if the art gap is reported again, the art issue still Resolved, then a review request containing Victor's Story and six briefs, then either canonical Story and task IDs or a revision request naming the task and correction. Keep the business until this passes.
2. **Fresh business.** Install Technical Director 6.13.1 first. Expect no Epic that owns only constraints and a first eligible candidate that is buildable work. Follow the run through the first accepted increment, not only sprint start. Include a duplicate wake, an unrelated resolved issue and a material scope change.

## Not done

- **Challenging a prerequisite on an accepted roadmap.** Epic dependencies are frozen into the outline digest that the Producer, the Technical Director and the platform all validate. Letting Gabriel override one needs an amendment path across all three. Design it before coding.
- **Reopening with evidence.** The Producer still has no path to supply `ReopeningEvidence`. A resolved issue can only return through a changed approved source. Add the path when a real case needs it.
- **Wake binding.** `WakeCoordinationWaitsAsync` does wake a waiting planning commitment when a board-scoped session by the same initiator completes, and the run timing (proposal at 8:56:50, assessment at 9:00) is consistent with that. It has not been confirmed from the live wake receipts.
- **Deferred by agreement:** semantic criterion coverage and partial-Story publication.

## Follow-up at 11:00 p.m.: 6.21.3 installed, commitment still Blocked

Live read at 07:00 UTC November 17: Producer 6.21.3 was running, the board still had 11 Epics and no Stories or tasks, and Gabriel's planning commitment `ff6349bb-f202-43ef-bdae-60d394154519` was Blocked at revision 35, unchanged since 05:35:26 UTC. Gabriel's activity was attention reviews only. Marcus's "Review blocked work" item was repeating the same manager recovery and reporting no continuation capability.

Cause: `EnsureCommitmentAsync` returns early for a commitment blocked with "execution stopped unexpectedly" unless `ReadProgressiveContinuationAsync` supplies a fingerprint, and that fingerprint exists only after a published increment is accepted. A crash before the first Story is published could therefore never be released by the Producer. That gate ran before the planning-contract release added in 6.21.3.

Producer 6.21.4: a changed planning contract (now `scoped-planning-recovery.v10`) authorizes one reassessment of that exact commitment revision through the existing `AuthorizeContinuationRecoveryAsync` receipt. Another failure advances the revision and is not retried. Test: `AnUpdatedProducerReleasesAPlanningCrashOnceBeforeAnyStoryIsPublished`. 604 pass.
