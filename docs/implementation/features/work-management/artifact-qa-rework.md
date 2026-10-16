# Artifact QA rework handoff and claim grants

## Incident

Prism Break `VGD0CEEB9048-8` received changes requested from QA. Victor's rework
delivered artifact `8ef67ea4-9c00-4ded-a979-9f5dc8900ba1`, revision
`0bec0c65-8241-4fef-b20a-130a9d093a4e`, SHA-256
`4591bed1ef9e68127d86ce222530dcd878f4cf3208c57dab3cacad620cf4552a`.
At October 15, 2026 19:40 Pacific, QA instead requested the original artifact
`7bf1e26e-83af-4add-a974-e90f40190c1d`; the broker correctly denied it.
The replacement's read grant was also revoked at claim time, 268 milliseconds
after creation, because the claim context had not loaded producer attempts.

## Implementation

- `WorkOrchestrator.AssignmentOutcomes` binds artifact QA to the latest attempt
  of the current traversal's author. Status, stage/attempt identities, outcome
  and exact document reference must agree. Failed latest attempts cannot fall
  back to older success. Other stages retain ordered history for rework context.
- SDK `ArtifactDeliveryReview.ExecuteAsync` rejects ambiguous author history
  before reads or inference. Assignment evidence reaches the reviewer. API and
  feasibility claims require independent version-specific evidence; assertions
  in the document and proposed performance targets are not verification.
- `WorkOrchestrationBoardState.SynchronizeTaskArtifactGrantsAsync` loads all
  execution items, stages and attempts before reconciliation. Tracking queries
  preserve in-transaction transitions. Latest producer evidence must match.
  Old-cycle and paused grants are revoked; duplicate claims retain one grant.
- `WorkOrchestrator.ManagerDirectionsAsync` carries reasons from the board
  manager and active same-organization human operators whose retry was already
  authorized by `WorkOrchestrationService.RetryAsync`. Other agent assignees
  cannot direct their own verdict. Directions never waive acceptance criteria.

## Verification and rollout

SDK **4.3.3**: 429 tests, two sample tests, seven generated-template tests and
template self-test passed. QA **4.0.1**: 84 package-only tests and self-test passed.
NuGet metadata confirms the versions and QA's SDK dependency. Linux `.csab`
packing/verification passed. WorkManagement.Contracts is unchanged.

Platform: 62 focused tests passed with packaged SDK/work/Office/Isolation
dependencies; existing Memory siblings remain enabled. `ArtifactReviewReworkTests`
covers shuffled cycles/attempts, latest-failure rejection, a fresh claim context,
current-grant preservation, old-grant revocation, replay and pause for both author
stage names. `ArtifactTaskDeliveryHandoffTests` covers production SDK callbacks,
ambiguity rejection before I/O and supplied verification evidence.
`WorkDispatchRecoveryTests` covers manager and authorized human retry directions.

Publish SDK before QA. Deploy API and AgentHost before installing QA and retrying
the blocked quality stage. Preserve the replacement document and previous attempts.
Supply pinned API source evidence through the authorized retry reason. Verify the
audit names the replacement artifact/revision and inspect the substantive verdict.
A real rejection returns to author rework; it must not be converted to a pass.

Live recovery on October 15, 2026: the owner installed QA 4.0.1 and retried, but
the still-running old platform supplied ambiguous history. SDK 4.3.3 correctly
rejected it before artifact access. API and AgentHost were then rebuilt and
restarted at 20:24 Pacific. Both deployment builds passed without warnings.
An explicit retry through `WorkOrchestrationService.RetryAsync`, with the existing
human operator's board authorization and pinned Phaser source evidence, dispatched
quality attempt `6100c793-382b-44cb-880e-b26d6c9ad62a` at 20:26 Pacific.
The broker audit confirms QA 4.0.1 successfully read the replacement artifact and
revision above. Its current `artifact.read` grant survived claiming the stage;
the old document grants remain revoked.

At 20:28 Pacific, Rohan completed that exact-revision review with
`changes_requested`: six invalid Phaser API references (bloom, chromatic
aberration, particle count/cap and sound stop/resume), a false native-effects
feasibility conclusion, and an unmeasured iGPU capability assertion. The verdict
cites the supplied v3.90.0 sources and distinguishes proposed budgets from
measurements. It passed repository/branch guidance but rejected four other
criteria. The orchestrator advanced to traversal 2 `specialist-execution` for
Victor's rework and revoked the QA grant only after the review completed.
This proves the repaired handoff and grant lifecycle; the document is not approved.

## Progress-aware rework

`WorkReworkPolicy.Evaluate` replaces the lifetime traversal cap for review-to-author
edges with the configured number of consecutive stagnant review cycles. A new
candidate must improve the best review's satisfied criteria (without losing any),
or reduce its finding count without criterion regression. The criterion sets must
match. A new revision identifier alone is not progress: immutable content/commit
digests must differ, and cycling back to previously reviewed content does not reset
the counter. Criterion text and finding counts are conservative structured signals;
this is not semantic proof that every remaining issue is unchanged. Unknown evidence
does not establish progress. Non-review workflow loops retain their original bound.

`WorkOrchestrator.AdvanceAsync` and `WorkOrchestrationService.CompleteStageAsync`
use the same policy for agent and human reviews. Stagnation preserves the accepted
review and blocks for management direction. `WorkReworkPolicy.QueueEscalationAsync`
persists a deduplicated `stage.rework.stalled` event and board-manager notification
in the same save as the blocked state and realtime outbox.

`WorkOrchestrationService.RetryAsync` recognizes the old traversal-limit failure
and, after its normal authorization/concurrency checks, resumes the accepted review
when progress allows it. It creates the next author stage and retry event together;
it does not create another QA attempt. Completed attempts and findings are retained.

Regression coverage: `WorkReworkPolicyTests` exercises long incremental progress,
criterion gains with newly discovered findings, three stagnant cycles, regression,
oscillation, repeated content, missing evidence and non-review limits.
`WorkDispatchRecoveryTests` exercises both completion paths, durable escalation and
authorized/idempotent recovery from a fresh database context.

Verification on October 15: all 56 focused policy, dispatch, hierarchical runtime
and artifact-handoff tests passed against packaged SDK/work/Office/Isolation
dependencies. No public contract or agent package changed. Existing dependency
advisory and unrelated compiler/analyzer warnings remain.

The local app restart loaded this change at 21:23 Pacific. The owner's board
action at 21:24 recorded `stage.retry.requested` with `resumedAcceptedReview: true`,
completed the previously stopped quality stage, and dispatched Victor's
`specialist-execution` at traversal 4. No new QA attempt was needed. The original
v4 review and its three remaining findings are preserved; delivery remains subject
to the next independent review.
