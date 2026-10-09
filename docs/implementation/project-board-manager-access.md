# Project board manager access

Last updated: **2026-10-08 PDT**.

## Incident

Prism Break (Super Awesome Games) stalled right after the production brief was accepted. Gabriel, the Producer, created
the project board, the sprint and 19 tickets. Every one of his staffing reviews then failed at once:

`PlatformCapabilityException {"code":"Denied","message":"The project delivery grant is required: work.delivery.read.v1"}`

The call path was `ReconcileWorkflowRecoveryAsync` → `StaffWorkflowAsync` → `HierarchicalProjectDelivery.PrepareAsync`
→ `ReadDeliveryPlansAsync`, and the error was thrown by `WorkDeliveryService.AuthorizeProjectAsync`. No ticket was ever
assigned, and the commitment blocked itself with the generic "execution stopped unexpectedly".

## Root cause

Workstream-scoped `work.delivery.*` grants are written only by `ProjectSetupService.ApplyParticipantsAsync`, which runs
during project setup, `PrepareDeliveryAsync` and hire enrollment. `PrepareDeliveryAsync` requires the manager to lead
the team, so a Producer on a team led by the Creative Director creates the board directly through `work.board.create`.
That path enrolled nobody:

- The accountable manager was never a participant, so he held no delivery grant on his own project.
- Participants enrolled before the board existed (Victor) got neither board grants nor delivery grants.

## Fixes

- **Platform:** `ProjectSetupService.ReconcileProjectBoardAccessAsync` (`ProjectSetupService.BoardAccess.cs`) runs
  under the organization lock whenever a project board is created. Agents create boards through
  `WorkManagementCapabilityHandler.CreateBoardAsync`; people create them through `WorkBoardService.CreateBoardAsync`.
  - It enrolls the accountable manager, who receives every delivery capability, as `ApplyParticipantsAsync` would give.
  - It gives every active participant their missing board grants and project delivery grants.
  - It never restores a participant a human removed or a revoked grant. A manager who is active on another project is
    left for an explicit decision.
- **Platform:** the hire-enrollment backfill for participants enrolled before the board existed now adds delivery
  grants as well (`AddMissingParticipantAccessAsync`).
- **Platform:** `AgentTicketFeedback.FailureSentence` reports `code=…denied` failures as missing access, names the
  capability, and points to Manage members. It no longer says "execution stopped unexpectedly".
- **Producer 2.18.2:** a denied capability during delivery recovery no longer crashes the commitment.
  - The commitment waits with the reason and rechecks every 20 minutes.
  - The refusal is raised once per board and capability to the Producer's manager.
  - The new reply `Retry staffing: <what changed>` from the reporting chain makes the Producer recheck immediately.
- **Creative Director 1.17.5:** the delivery-escalation relay also forwards messages carrying `Retry staffing:` to the CEO.

## Follow-up after the first rollout (2026-10-08 evening)

The first fix didn't unstick the live Prism Break, for two reasons:

- **The board already existed.** Reconciliation ran only when a board was created. It now also runs on every
  `work.orchestration.profile.configure`, which the Producer calls at the start of every staffing pass, so existing
  project boards are repaired on the next review.
- **The refusal looked like an outage.** The broker returns handler refusals as a `PlatformCapabilityError` payload
  (`{"code":"Denied",...}`) with no failure code. The gateway sent `capability.failed`, and the SDK's
  `McpAgentRuntimeClient` raises every tool error as `Unavailable`. So Producer 2.18.2 never recognized the refusal, and
  the ticket still said "execution stopped unexpectedly".
  - `McpGatewayEndpoints.FailureCodeFromPayload` now sends `platform.capability.<code>`, for example
    `platform.capability.denied`.
  - Producer 2.18.3 also recognizes the `{"code":"Denied"}` payload.
  - Follow-up: the SDK should map the payload code to `PlatformCapabilityException.Code` itself.

## Tests

- `ProjectBoardAccessTests` (6): exercises `WorkDeliveryService.ReadAsync` end to end with the real authorization service.
- `AgentTicketFeedbackTests.APlatformRefusalNamesTheMissingAccessInsteadOfAnUnexpectedStop`.
- Producer `DeliveryAccessTests`.
- Creative Director `DeliveryEscalationRelayTests.A_producer_access_refusal_is_relayed_too`.

## Recovering an existing project

As an Owner or Manager, add the Producer under **Projects → Manage members**. Then retry the blocked
"Maintain delivery assignments" task, or send the Producer `Retry staffing: <what changed>`.
