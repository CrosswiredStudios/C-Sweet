# Producer ↔ Creative Director handoff stall

Last updated: **2026-10-08 PDT**. The two incidents below have different causes. The October 2 fix does not establish recovery for the October 7 memory-reset failure.

## October 7: memory reset followed by missing failed-session recovery

**Status: fixed in source and locally verified (2026-10-08); live acceptance pending.** See [Memory-reset handoff recovery](#memory-reset-handoff-recovery-2026-10-08) below. The paragraphs in this section describe the original diagnosis. The user reports Gabriel sent five production-brief scope questions on October 7 at 6:24 p.m. PDT (October 8 at 01:24 UTC). Naomi's reply ended with `memory.retained_evidence_invalid`, the platform replaced her runtime, and the collaboration became Failed. Agents continue scheduled reviews, but the exact production brief remains unaccepted.

`CreativeDirectorNextStep.Describe` (`NextStepReporting.cs`) reports “the Producer and I converge on the shared production brief” whenever `HandoffSessionId` exists. The project setup branch in `VideoGameCreativeDirectorAgent` creates a handoff only when that ID is null; it does not inspect Failed there. `VideoGameCreativeDirectorAgent.FindProducerKickoffsAsync` (`ProducerKickoff.cs`) likewise skips states with an existing ID. These paths can leave a failed session represented as ordinary waiting work, so successful scheduled reviews do not recover it or enter the exception-based stall escalation.

`AgentCoordinationService.RecoverTransientFailuresAsync` currently admits selected structured transient transport/rate-limit/timeout or unavailable-inference failures. It does not automatically resume memory resets. `ResumeAsync` and `ResumeForManagerAsync` already implement revision-bound, idempotent Failed/Blocked recovery, preserving the transcript and retrying the failed speaker. An authorized human manager can use **Retry collaboration** in Communications after reviewing the failed work and any effects already committed. That is an immediate recovery option, not the missing agent-state-machine fix.

The precise memory trigger in the reported live run is **unverified**. `PlatformMemoryReadEvidence.AuthorizeDispatchCoreAsync` validates all retained reads for the runtime and collapses several authorization/integrity/parser failures to `MemoryRuntimeResetRequiredException.RetainedEvidence`. Changed sources or authority, or carrying private chat context into another work/audience, can legitimately require replacement. The error does not alone prove memory corruption. Do not disable the privacy check or blindly replay previously delivered work.

Required work: record a content-free specific validation reason; inspect the authorized current handoff status during durable wakes and bounded reviews; resume only eligible failed execution after replacement readiness, using the current revision and a stable failed-delivery recovery key; bound retries and escalate; preserve pending questions and exact artifact acceptance before staffing. Blocked semantic outcomes, cancelled sessions and revoked authority must not be treated as generic transient failures. Tests must cover prior private chat context, replacement readiness, all session statuses, duplicate/concurrent recovery, participant/grant revocation, committed effects and repeated failure.

The full handoff, code map, remaining scope and acceptance requirements are in [agent memory hardening — AI handoff](features/agent-memory-hardening.md#ai-handoff-current-starting-point-and-completion-contract). This is tracked within existing A7/C7 and operational visibility requirements; no milestone has been marked complete.

## October 2: null work-context health-capture crash

## Memory-reset handoff recovery (2026-10-08)

The Prism Break handoff failed after Gabriel's five production questions when Naomi's runtime
was fenced with `memory.retained_evidence_invalid`. The session remained `Failed` while project
reviews reported convergence because `HandoffSessionId` was populated.

- Creative Director 1.17.3: `CheckProducerHandoffAsync` (`HandoffReview.cs`) reads the current
  session before portfolio reconciliation. Failed, blocked and cancelled handoffs enter
  `ReportReconcileFailureAsync`, which reports the actual status, reviews after ten minutes,
  and escalates once after three consecutive failed reviews. A saved session ID is not evidence
  that collaboration is progressing.
- `AgentCoordinationService.RecoverTransientFailuresAsync` recovers eligible memory-reset
  delivery failures after a one/two-minute cooldown, with at most three total execution attempts
  for the unanswered logical turn. It requires the failed attempt's runtime to have confirmed
  reset completion and termination, no pending reset, and that any active runtime for the
  installation is a different runtime with the current evidence format, plus active
  participants/installations and current coordination grants. A Running replacement is not
  required: on-demand installations start a runtime only once work is pending, so requiring one
  would leave the session Failed indefinitely. The resumed turn is claimed by a fresh runtime. Erasure resets
  remain outside automatic recovery. `ResumeAsync` preserves the session, transcript, artifact
  revisions and unanswered speaker, and creates a new revision-bound delivery rather than replaying
  the old lease. Exhausted/ineligible failures remain Failed and enter the Director escalation path.
- `PlatformMemoryReadEvidence.AuthorizeDispatchCoreAsync` records a bounded validation code,
  receipt ID, current work ID and exception type on the reset's `AgentRuntimeEvent.Reason`.
  `MemoryRecallDispatchEvidence` tags source eligibility/closure changes and queued-recall
  consumer-kind/audience failures. `AgentMemoryRuntimeReset` saves this diagnostic atomically
  with runtime/session fencing. No source content or raw exception message is copied.

- Root-cause prevention: `AgentWorkInbox.ClaimAsync` now calls
  `MemoryRecallDispatchEvidence.RequireRetainedConsumerAsync` before leasing any work to a runtime
  that retains memory read receipts. Retained queued-chat context, relationship-private broker
  reads and shared-audience reads are checked against the candidate work's audience
  (`AuthorizeRetainedDeliveryAsync`, `RequireRelationshipConsumerAsync`,
  `AuthorizeChatReadSharedAudienceAsync`). If the candidate may not receive that context — for
  example a coordination turn, or another human's chat, after a private chat recall — the runtime
  reset is staged with a `validation=claim.*` diagnostic and the work stays **Pending and
  undelivered**. A fresh runtime then claims it, so the collaboration does not fail at all.
  Dispatch authorization remains the final check; the claim-time check only avoids delivering
  doomed work. This matches the most likely trigger of the October 7 incident (Naomi answered a
  private chat with recalled context, then received the coordination turn on the same runtime);
  the exact branch of that historical reset cannot be reconstructed.

### Confirming the October 7 trigger (content-free)

Run on the affected database (read-only). These select identifiers, states and server codes only; they never read turn content or memory payloads.

```sql
-- 1. The failed production-brief session(s) around 2026-10-08 01:24 UTC.
SELECT "Id", "Status", "Revision", "UpdatedAt", "InitiatorOrganizationUserId", "TargetOrganizationUserId"
FROM "AgentCoordinationSessions"
WHERE "Status" = 'Failed' AND "UpdatedAt" BETWEEN '2026-10-08 00:30Z' AND '2026-10-08 04:00Z'
  AND "FinalSummary" LIKE 'The agent''s memory context changed%';

-- 2. The failed delivery and the runtime that received it (replace :session).
SELECT w."Id" AS work_id, w."Status", w."AttemptCount", a."Attempt", a."RuntimeInstanceId", a."Error", a."ClaimedAt", a."FinishedAt"
FROM "AgentWorkItems" w JOIN "AgentWorkAttempts" a ON a."AgentWorkItemId" = w."Id"
WHERE w."SourceType" = 'agent-coordination' AND w."CorrelationId" = ':session' ORDER BY a."ClaimedAt";

-- 3. That runtime's reset and the earlier work whose memory it still held (replace :runtime).
SELECT "MemoryResetReasonCode", "MemoryResetRequestedAt", "MemoryResetCompletedAt", "Status" FROM "AgentRuntimeInstances" WHERE "Id" = ':runtime';
SELECT r."Capability", r."WorkId", w."SourceType", r."CreatedAt"
FROM "AgentMemoryReadReceipts" r LEFT JOIN "AgentWorkItems" w ON w."Id" = r."WorkId"
WHERE r."RuntimeId" = ':runtime' ORDER BY r."CreatedAt";
```

A receipt with capability `platform.memory.queued-recall.v1` (or a relationship-partition broker read) from a `chat-turn` work item that precedes the coordination delivery confirms the expected audience invalidation: private chat context was still held when the coordination turn arrived. With this change that delivery would have been held back and claimed by a fresh runtime. Resets recorded after this change also carry `validation=...` in `AgentRuntimeEvents."Reason"`.

Communications **Retry collaboration** remains an explicit recovery option for authorized managers
after reviewing the failed turn. It resumes the existing transcript and is separate from automatic
review recovery. New diagnostics cannot reconstruct the precise validation branch of an old reset.

Regression coverage: `HandoffReviewTests`, `AgentCoordinationServiceTests.MemoryRecoveryRequiresTerminatedContaminatedRuntimeAndPreservesQuestions`
(including no active runtime and a legacy-format replacement), `MemoryResetDistinguishesUnavailableEvidenceFromChangedRetainedEvidence`,
`QueuedRecallResetRecordsTheSpecificNonChatConsumerValidation` (coordination turn stays pending) and
`QueuedMemoryDeliveryCannotReusePreviousRecipientContextForAnotherHuman` (another human's chat stays pending).

## Evidence

Business "Super Awesome Games": Naomi Chen (Creative Director 1.13.0) had an accepted Tetris pitch and an
approved staffing request (`39a8a7be…`). Gabriel Reyes (Producer 2.14.0) was hired at 00:06 UTC and sent his
kickoff DM. Naomi's turn failed ("The agent couldn't complete that request"). Her
`add_personal_todo` call for "Prepare and share project documentation with the Producer" returned `Conflict`:
"The requested operation requires an element of type 'String', but the target element has type 'Null'."
Every five-minute project review repeated the same failure (12+ times) while the review card still read
"Project reconciliation completed". Gabriel's reviews found an empty portfolio and returned silently.

## Root cause

`CSweetDbContext.CaptureProjectHealthSignals` (`CSweetDbContext.ProjectHealthCapture.cs`) runs inside
`SaveChangesAsync`. `HealthProjectForTask` called `JsonElement.TryGetGuid` on
`PersonalWorkContextJson`'s `workstreamId`, which is JSON `null` for unscoped work. `TryGetGuid` throws
`InvalidOperationException` for non-string values, so any personal task created with a work context failed
to save. `CaptureIncidentFailure` had the same pattern for `incidentId`.

## Fixes

- Host: `ReadGuidProperty` checks the value kind before `TryGetGuid`, and each health signal is captured in
  isolation so advisory capture can never veto the business mutation. Tests:
  `ProjectHealthTests.OptionalGuidReadsNeverThrow`, `PersonalWorkWithNullContextScopesStillSaves`.
- Creative Director 1.14.0: project reviews name the next step (`CreativeDirectorNextStep`), never report a
  failed reconciliation as completed, record consecutive failures (`creative-reconcile-stall:<conversation>`)
  and DM the CEO once after three. A kickoff whose handoff task cannot be queued is answered with the next
  step instead of a failed turn. Pitch-brief replies tolerate fenced or trailing provider text.
- Producer 2.15.0: the kickoff names the handoff the Producer needs, and `producer-handoff-watch` makes an
  empty-portfolio review follow up with the manager (each follow-up is also a Director kickoff wake) and then
  tell the owner once.

## Convention: every waiting agent knows its next step

An agent that is not making progress must be able to answer, from durable state alone: what is the next
step, who owns it, since when, and what failed last. Waiting results and review cards state that next step;
failures are recorded per step and retried; a step that keeps failing, or a counterpart that stays silent, is
escalated once up the reporting chain. Events remain wake hints only, so a missed or failed event is
recovered by the next review rather than by a human noticing silence.

## Recovery for the October 2 incident

Restart the rebuilt host. Naomi's next review creates the documentation task and the handoff resumes; no
data repair is needed. Import Creative Director 1.14.0 and Producer 2.15.0 for the reporting and follow-up
behavior.
