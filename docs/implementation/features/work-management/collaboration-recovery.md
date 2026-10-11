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
