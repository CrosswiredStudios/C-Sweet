# Delivery rejection handoff and board retry (2026-11-12)

Seen at QFN Games on 2026-11-12. The Prism Breaker Sprint 1 increment acceptance (`VG58E8BFBD4D-32`) was recovered
once, after a "no runnable test package" rejection. Its technical review was then rejected again, on the same
candidate, with real findings: `scripts/verify-pin.mjs` did not compare the lockfile root dependency with
`package.json`. Nothing happened afterwards:

- The board showed no comment for the new findings.
- The Producer was never handed a correction, so no corrective task was planned.
- Moving the blocked ticket to a working column was accepted and reverted within a second, and nothing re-ran.

## Causes

1. **No correction for a second rejection of the same candidate.** `EnsureCorrections` skipped an execution when any
   correction already existed for its execution and candidate digest, including a Completed one. A recovered candidate
   that was rejected again therefore never produced a new handoff, and the release manager's correction coordinator
   (SDK `DeliveryCorrectionCoordinator`, which acts on non-completed corrections) had nothing to act on.
2. **No ticket comment for a second rejection.** `RecordFindingsAsync` keyed its comment on execution and candidate
   only, so the second rejection's findings were never posted.
3. **A board move did not retry a delivery ticket.** `WorkBoardService.MoveItemAsync` retries a blocked sprint card
   moved to a ready or working column, but it treated a declared delivery ticket as an ordinary card.
   `SynchronizeTicketAsync` then put the ticket back in Blocked on the next update.

## Behavior now

- `EnsureCorrections` treats findings as handed off only when an existing correction for the execution and candidate
  already carries one of the current open findings. New findings on the same candidate get a new correction and a
  `correction.requested` event. This also repairs plans already in this state on the next delivery pulse.
- The finding comment key includes a digest of the rejection's findings
  (`delivery-finding:{execution}:{candidate}:{findings}`), so each rejection is posted once.
- `IWorkDeliveryService.MoveDeclaredTicketAsync` handles a person moving a declared delivery ticket.
  - **A blocked ticket moved to a ready or working column**, by the release manager or an organization owner, retries
    the check it stopped at, exactly like Retry validation on the project's Releases page, but only for that ticket.
  - **Any other manual move** is refused with guidance, because the plan owns the ticket's column.
  - Items that are not declared delivery tickets of an active or paused plan move as before.
- Retrying a stopped check, from Retry validation or a board move, supersedes its open findings and also closes the
  correction handed off for them (unless it already has a follow-up plan). The manager is no longer asked to plan a
  repair for a check that is being re-run.

## Tests

`DeliveryComputeGenerationTests`:

- `Moving_a_blocked_delivery_ticket_to_a_working_column_retries_the_check_it_stopped_at`
- `A_candidate_rejected_again_after_recovery_hands_off_and_posts_the_new_findings`

The existing retry, correction and board tests still pass. The board hook in `WorkBoardService` is covered through the
service method; the delivery test fixture has no board columns.
