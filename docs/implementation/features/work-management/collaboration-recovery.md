# Commitment collaboration recovery

`WorkItemMutationEngine.RequeueAsync` connects an authorized Blocked-to-To-Do move
to the board collaboration on which that commitment depends. A board specialist's
Blocked response without an artifact requires a fresh specialist response;
`AgentCoordinationService.ResumeAsync` no longer asks the initiator to summarize
the same old blocker. A blocked response containing an artifact retains the
existing initiator review path. Chat collaboration behavior is unchanged.

`WorkItemMutationEngine.CoordinationRecovery.cs` implements the shared recovery
path without role names or failure-message matching. Correlation requires the
same organization, board, initiating employee and installation, project/team
scope, and either an exact coordination session or an exact source fingerprint.
Ambiguous discovery is bounded and declines to choose an arbitrary session.

`RecoverBlockedCoordinationCommitmentsAsync` reconciles durable current state:
a newer Active/Summarizing/Completed collaboration wakes its blocked owning
commitment. An enabled specialist installation updated after a missing-response
block triggers one fresh response. An unchanged blocker, an unrelated update,
a disabled specialist, or revoked coordination capabilities does not trigger an
automatic retry. Human requeue remains available through existing personal-board
grants. Recovery does not supply approvals or expand permissions.

The collaboration resume, inbox delivery, commitment transition and availability
outbox commit in the same relational transaction. Revision checks and deterministic
resume keys protect against replay; inbox delivery is idempotent. Existing terminal
coordination wake events and bounded reconciliation provide attention/reconnect
recovery; no agent polling loop was added.

`RecordCommitmentDiscussionAsync` projects authenticated collaboration turns into
the owning ticket's comments, with a link to the full conversation. These are
identified as platform transcript copies, not newly authored agent comments.
Turn IDs deduplicate both persisted and locally pending projections. Comments and
their UI wake hints commit together; board read grants control visibility.
The recovery comment records that a fresh specialist attempt was requested.

`EnqueueTurnAsync` includes the authorized recovery reason in the durable delivery
payload. The commitment recovery prompt asks the specialist to explain the issue
to its counterpart, propose a bounded remedy, create or reuse repair work through
existing authorized capabilities where appropriate, and report owners, ticket
references, verification and remaining decisions. This is guidance; a model's
proposed repair is not proof that work was created or that the blocker is resolved.
`AgentCoordinationService.RespondAsync` calls `RecordCoordinationDiscussionAsync`
so new diagnostic and outcome turns, their ticket comments and UI wake hints
commit with the response. Reconciliation also catches up older missing transcript
copies after reconnect or platform update.

Tests: `AgentCoordinationServiceTests.BlockedRetryRequestsFreshSpecialistOnlyForMissingBoardResponse`,
`ManagerTicketRequeueResumesSpecialistAndPreservesVisibleDiagnosis`, and
`CommitmentAttentionRecoversOnlyRelevantCurrentChanges` cover routing, durable
recovery instructions, manager requeue, correlation, changed/unchanged inputs,
disabled/revoked participants and duplicate-free discussion projections.

Deploy by rebuilding/restarting the platform API and AgentHost. No schema migration
or new agent package is required. Existing correlated blocked commitments recover
on reconciliation after a relevant specialist update, or through a manual move to
To-Do. Runtime restart alone cannot repair an unchanged invalid generator.

`AgentCoordinationService.RecoverTransientFailuresAsync` also recovers a coordination
delivery that expired before any claim while its agent was offline. The inbox's
`AgentWorkInbox.DeadlineElapsedBeforeClaimReason` identifies this platform failure;
recovery requires zero attempts, no lease/attempt records, an elapsed deadline,
a connected Running runtime for the intended speaker, and current participant
identity and coordination grants. Disabled, cancelled, revoked, and actually
attempted work are excluded. The normal recovery cooldown applies.

`ResumeAsync` preserves the transcript and expired delivery receipt, reuses the
pending speaker and causation, and enqueues a new delivery with a fresh deadline
and a deterministic recovery key. It does not reopen the expired lease or extend
its old deadline. The existing recovery dispatcher discovers eligible failed
sessions after reconnect; duplicate discovery does not create another delivery.
`AgentCoordinationServiceTests.ExpiredUnclaimedCoordinationRecoversOnlyConnectedAuthorizedSpeaker`
covers connected/offline, claimed/attempt-record, disabled/revoked, cancellation,
execution timeout, cooldown, and duplicate discovery cases.

## Runtime attempt diagnostics and recovery

`AgentWorkInbox.RecordTicketFailureAsync` distinguishes the inbox attempt budget
from the pinned sprint stage budget. A dead-lettered single inbox delivery can
still have a scheduled stage retry; its comment must not tell the user that manual
intervention is required while that recovery remains available. Repeated failures
still park the ticket and escalate through `AgentTicketFeedback.RecordFailureAsync`.

Lease-expiry comments retain the work/runtime IDs, attempt number, last heartbeat
and lease expiry. Runtime causes use fixed explanations for known lifecycle states,
never raw provider logs or exception messages. Valid structured diagnostic GUIDs
are retained for correlation. `WorkOrchestrationBoardState.RecordClaimAsync` clears
the previous stage error/retry timer and calls `AgentTicketFeedback.RecordRecoveryAsync`
to append an idempotent recovery comment with an atomic UI outbox wake. The leased
branch of `WorkOrchestrator.ReconcileAttemptAsync` also repairs already-running
retries with stale errors after deployment. Failure comments and attempt records
remain historical evidence; a recovery comment confirms a claim, not completion.

`AgentRuntimeStartupCleanupService.CleanupAsync` preserves a fleet runtime only
when it has current memory evidence, no reset request, an unexpired runtime deadline,
an authenticated current session and a live work lease belonging to that installation.
Other stale runtimes remain eligible for cleanup. Before cleanup stops a workload it
persists `RestartCleanupReason`, so later diagnostics can identify the restart instead
of merely reporting “Runtime destroy requested.”

`ComputeDefaultsService.RequireProjectAsync` resolves current authorized standard
project boards without requiring the legacy `ProjectDeliveryBinding`. It retains
explicit participation, active team/project and board grant checks, and refuses an
ambiguous board selection. `ComputeCapabilityHandler` returns bounded, specific
project board/membership recovery messages instead of hiding these failures behind
`compute_conflict`; exception text is not exposed.

Rebuild and restart AgentHost and WorkerHost to apply these platform fixes. No agent
package update or schema migration is required. An existing healthy retry should be
allowed to finish; requeue only a ticket whose current stage is actually blocked.
