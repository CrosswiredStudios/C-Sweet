# Chief of Staff event inventory

Audited October 23, 2026 against Chief candidate 2.14.1-rc.2 and SDK 5.4.0.
The manifest is a delivery contract. An SDK-owned subscription must be inspected at its
runtime entrypoint; testing `ChiefOfStaffAgent.HandleEventAsync` alone does not exercise it.

| Subscription | Runtime owner and observable effect |
|---|---|
| `com.csweet.calendar.reminder-due.v1` | `AgentRuntimeWorker.HandleEventAsync` → `ChiefOfStaffAgent.HandleCalendarReminderAsync`. Reads the current authorized occurrence, ignores canceled/moved/foreign hints, sends one owner reminder per stable reminder identity. Scheduler-owned work remains scheduler-owned. |
| `com.csweet.agent.attention.review-due.v1` | SDK worker → manager incident recovery → `HandleManagerAttentionReviewAsync`. Recovers project assistance, approved resource changes, hiring queue, retained PM delivery and leadership agenda, then SDK drains at most three eligible personal tasks. |
| `com.csweet.work.personal-todo.available.v1` | SDK `DrainPersonalTodoAsync`, bounded claims and canonical result submission. Chief assessment cards are excluded by `EvaluatePersonalTodoClaimAsync`; resource-review cards reconcile current source and return blocked/completed without model inference. |
| `com.csweet.work.delegation.received.v1`, `com.csweet.work.tracking.changed.v1` | SDK `DelegationRuntime.DispatchAsync` reads current authorized delegation/tracking state and owns its durable protocol. |
| `com.csweet.management.incident.owner-report-requested.v1` | SDK `ManagerIncidentCoordinator.DispatchAsync` reads the current incident, generates the scoped owner report, and records presentation state. |
| `com.csweet.project-health.review-due.v1`, `com.csweet.management.incident.changed.v1` | Sealed manager entrypoint → `ProjectIncidentReview.HandleManagerAsync`. Current diagnostics, bounded manager disposition and existing escalation authority. |
| `com.csweet.chief-leadership.changed.v1` | `ProcessLeadershipAsync`. Re-reads current agenda, recovers native answers and pending delivery, interprets at most one eligible assessment, validates through Core, persists source/card/outbox atomically. |
| `com.csweet.agent.configuration-choice.answered.v1` | `HandleConfigurationChoiceAnsweredAsync`. Recovers the saved operating-profile decision and continues onboarding using its effective profile. |
| `com.csweet.agent.onboarded.v1` | `HandleOnboardedAsync`. Profile assessment → hiring preferences → native focus decision, with stable source/message identity and explicit lifecycle completion. Required focus failures remain unacknowledged. |
| `com.csweet.user.message.received.v1` | Current-message validation → authenticated leadership/native hiring answers → explicit settings changes → bounded conversation/tool policy → required delivery → final commit. Response prose cannot create staffing. |
| `com.csweet.management.review.due.v1` | `HandleManagementReviewAsync` assembles current operating context and submits a status report bound to the management request/cycle. |
| `com.csweet.management.resource-change.requested.v1` | `DispatchResourceChangeAsync` → current request and reporting-chain reads → approve or request revision. Permanent failures become durable review work; transient failures propagate. |
| `com.csweet.management.resource-change.decided.v1` | Same dispatcher → current approved request → scoped hiring/withdrawal and manager brief. Superseded decisions cannot revive old effects. |
| `com.csweet.hiring-recommendation.fulfilled.v1` | Validates installation/organization and fulfillment shape, resumes the source-linked queue, applies Game Studio ownership policy and resumes project assistance. Leadership recovery independently verifies actual coverage. |
| `com.csweet.action.completed.v1`, `com.csweet.action.rejected.v1`, `com.csweet.approval.completed.v1`, `com.csweet.workstream.changed.v1`, `com.csweet.workforce-plan.decided.v1`, `com.csweet.workforce.changed.v1` | Current-state PM context reconciliation and leadership recovery. Workforce changes additionally reconcile conflicting Game Studio recommendations. Stable delivery intent/ack preserves receiver idempotency keys. |

## Deliberately unsupported intake

The manifest no longer subscribes to `com.csweet.communication.message.mentioned.v1`,
`com.csweet.management.status.reported.v1`, or `com.csweet.management.resource-need.reported.v1`.
Their old callbacks acknowledged events without implementing the promised workflow. Durable
manager-report/mention intake and reporting obligations belong to Phase 4; do not restore these
subscriptions until authenticated source reads, persisted outcomes, recovery and dispatch tests exist.

## Failure and recovery boundary

`ChiefOfStaffAgent.ResourceRecovery.cs` isolates expected permanent failures per request. The
operating-state record precedes its review card, duplicate creation uses a stable episode key,
and a claimed review task re-reads source status before returning a result. Source resolution
is recorded before requeuing blocked cards; later recovery repairs a missed requeue. It never
uses model prose as completion evidence. Unknown defects, unavailable services and cancellation
are not acknowledged as permanent blocks. A missing persistence/card grant fails delivery
instead of claiming that the review obligation was saved.

The reserved all-zero identity in `chief-resource-review:` denotes the bounded hiring-discovery
workflow, never an actual resource-change ID. It retains access failures that occur before a
request can be enumerated. Retrying that card re-runs current approved-plan/backlog discovery;
it does not submit a zero-ID domain command or infer that hiring itself has completed.

The generic personal-task prose protocol and broad reporting obligations remain Phase 4 work.
The inventory does not claim installed published-candidate/UI acceptance. See
[implementation evidence](chief-of-staff-quality-improvement-evidence.md) for retained test results
and [the phased plan](chief-of-staff-quality-improvement.md) for the G3 rollout boundary.
