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
