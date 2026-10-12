# Delegation with tracking

Status: implemented and locally verified; browser and live rollout acceptance remain pending. October 12, 2026.

## Product blocker and acceptance journey

The observed Communications request asked Naomi to tell Gabriel to retry staffing after gateway
and recovery fixes. `CreativeDirectorInteractionPolicy` classified the outer request as ordinary
workflow input; the production-oversight fallback returned a canned status. No delegation or
reporting responsibility was persisted. This slice addresses that product blocker without claiming
closure of broader Memory milestones or displacing unrelated M07 closure work.

The requester asks Naomi; Naomi persists the instruction and reporting commitment before saying
she will ask Gabriel and keep the requester updated. Gabriel accepts, clarifies or declines under
current authority. Acceptance creates or reuses canonical work and states the intended action.
Naomi reports a concise summary and work link. Meaningful changes wake her to report progress,
decisions, blockers and the supported final outcome. Tracking closes only after the final report
commits. Creating, forwarding, requeueing or acknowledging work is never sufficient evidence of
the intended outcome.

## Code and ownership

| Boundary | Files and members |
|---|---|
| Public contracts | `CSweet.WorkManagement.Contracts/DelegationContracts.cs`: `DelegateWithTrackingRequest`, `DelegationResponse`, `DelegationSnapshot`, `TrackingUpdate` |
| Typed SDK | `PlatformDelegationClient.DelegateWithTrackingAsync`, `PlatformWorkTrackingClient.FollowAsync`, `UnfollowAsync`, `PendingUpdatesAsync`, `MarkProcessedAsync` |
| Shared dispatch | `DelegationRuntime.DispatchAsync`, `ReportPending`; `AgentRuntimeWorker.HandleEventAsync`; matching `AgentTestRuntime.DeliverEventAsync` |
| Additive callbacks | `CSweetAgentBase.Delegation`: intent classification, recipient decision, native execution, work resolution, outcome review, summary, followed-work update |
| Application boundary | `ITrackedDelegationService` |
| Authoritative persistence | `TrackedDelegation`, `WorkTrackingSubscription`, `WorkTrackingUpdate`, `WorkTrackingOperationReceipt`; `CSweetDbContext.Delegation` |
| Platform operations | `TrackedDelegationService`; `DelegationCapabilityHandler`; `DelegationToolSchemas` |
| Wake and recovery | `WorkTrackingWorker`, `AgentPlatformEventDispatcher`, `MemoryRecallDispatchEvidence.CaptureMetadataAsync` |
| Human experience | `WorkTrackingEndpoints`, `WorkTracking.razor`, `WorkTrackingPage.razor`; project and employee personal work details |

Naomi owns coordination and report delivery. Gabriel owns execution through existing mutation
services. The reporting commitment is a non-executable personal management card handled through
shared SDK callbacks. It is not dispatched to an agent's specialist personal-task handler.
The linked execution item retains canonical identity across board transfers. Follow reads and
notifications recheck current resource authority. Delegation does not grant execution, board,
repository, staffing, model, memory or approval authority.

Default SDK callbacks read owned project and personal work, using bounded project discovery and
the exact linked item's current `WorkBoardId`. Rebinding retires the non-executable management
placeholder, preserves source provenance and keeps the original execution turn. Execution-message
discovery and durable `ExecutionHandled` receipts avoid dependence on the latest 50 updates or
outstanding delegations.

## Protocol

`work.delegation.{create,read,respond,publish,cancel}.v1` and
`work.tracking.{follow,unfollow,read,progress,pending,processed}.v1` are separate reviewed grants.
`com.csweet.work.delegation.received.v1` and `com.csweet.work.tracking.changed.v1` are durable
wake hints. Their IDs locate current state; their payloads are not authoritative snapshots.

Create accepts an exact bounded instruction selected from the current retained source message,
an intended outcome, authenticated recipient, source IDs, optional project/team/item context,
interested parties, parent delegation and an idempotency key. Quoted instructions are published
only when selected by the outer request. Structured classification precedes specialist fallbacks.
Mentions and directory identities resolve people; ambiguity receives a clarification.

Acceptance identifies canonical work and intended action. The normal persisted `ChatTurn` executes
the instruction using the existing recipient workflow and lease path. Shared SDK behavior records
its response as progress and can rebind tracking to the actual owned work. Role workflows retain
project-required development and approvals. Peer requests require acceptance; directives are
checked against current reporting relationships. Revoked or amended instruction sources fence
further execution and reporting.

## Reporting and recovery

Explicit acknowledgement, milestone, blocker, decision and final receipts are immediately eligible.
Ordinary progress waits five minutes and the coordinator consolidates due updates. Heartbeats,
leases, routine metadata and automatic replies do not produce substantive tracking progress.
Default attention thresholds are one hour awaiting acknowledgement and 24 hours without substantive
progress; an unresolved condition produces one attention update until meaningful state changes.

Updates and outboxes commit with business state. Duplicate effects reuse stable domain keys.
Final reports carry work-revision evidence. Reopened, rebound or amended work supersedes an
undelivered final receipt and wakes the recipient for review; it cannot close tracking using stale
completion evidence. The original receipt is retained without a false delivery marker.
Agent discovery is bounded and current-authorized; processing receipts support independent follows.
Human follows use `UserNotification`. Informational reports omit response turns to prevent loops.
Nested reports become upstream evidence, preserving the parent's intended outcome and responsibility.
Unfollowing and cancelling tracking do not cancel execution, including requests still awaiting
acknowledgement or a queued execution turn. `TrackingCancelledAt` closes reporting commitments
and delegation subscriptions while preserving the execution lifecycle. Snapshots expose
`TrackingCancelled` and `ExecutionStatus` separately; cancelled tracking cannot reactivate
subscriptions, publish further reports or produce stale-progress attention.

The limited tracking view contains explicitly shared acknowledgement/progress/result summaries
and stable work identity. It excludes private task bodies, execution receipts and case memory.
Full work links require full current task access. Stable record IDs, revisions, source hashes,
author IDs, timestamps, organization and project/team context provide later ingestion provenance
and applicability. No memory enrichment is required for recovery.

## Future continuity boundary

Original authorship, current coordinator, current recipient, work execution, coordination and
report delivery remain separate. Future assignment changes must pass through the platform service
boundary and fence old responsibilities. Role/team/project references describe context and never
select or authorize a replacement.

Inactive employees preserve outstanding records and trigger attention for the authorized manager.
This release does not choose successors, reassign execution, restore reporting relationships or
transfer private knowledge. Durable linked work, reporting destinations, pending receipts and
last-reported positions support later takeover without reconstructing private conversations.

Memory supplies rationale, decisions, preferences and lessons; delegation/work records determine
outstanding obligations. See [Memory System Implementation](memory-system-implementation.md) for
future source-linked ingestion, succession matching, private transfer constraints and milestone mapping.

## Rollout and acceptance

Publish additive platform capabilities and migrate persistence before dependent SDK 3.65.0,
Contracts 3.28.0 and agent packages. Existing untracked conversations remain compatible; no
historical delegation is inferred from private chats. New capability/event requests require reviewed
installation grants. Keep package-only validation distinct from sibling-source builds.
The Memory broker adapter is 0.5.1 solely to synchronize its SDK dependency; vendor-neutral
Memory packages remain 0.5.0 and no memory behavior is added by that compatibility patch.

Acceptance must cover the exact staffing journey, reused/new work, peer decisions, clarification,
nested requests, every first-party manifest, manual follows, limited projections, transfer and
revocation, amended sources, cancellation, duplicates/order, offline recovery, early completion,
response loss, provider failure, restart and employee deactivation. Use disposable PostgreSQL,
SDK/template, agent, API/component and browser/runtime tests. Correlate diagnostics by delegation,
work item, reporting commitment and receipt ID without instruction or message content. Record
verified results here before describing the slice as complete.

### Verified local results

| Check | Result |
|---|---|
| SDK solution, including template generation and sample | 383 SDK tests and 2 sample tests passed |
| WorkManagement.Contracts | 30 tests passed; package 3.28.0 inspected |
| First-party package-only release checks | 146 checks passed across SDK, contracts, Memory broker and 23 agents; agent self-tests and final release-note versions verified |
| Package inspection | 26 package versions inspected, including Memory broker 0.5.1; final SDK/contracts binary hashes match the package cache used by consumers |
| Final platform against packed SDK/contracts | 25 delegation/API/component/runtime tests passed, zero skips |
| Related work, tool and communication regression | 240 passed, zero failures; 3 existing test skips |
| Disposable PostgreSQL | Full additive migration chain, response-loss/restart replay, task/report deduplication, report/outbox rollback on a forced uniqueness failure, 30-update coalescing, and persisted cancellation with continued execution passed |
| Combined SDK/platform staffing journey | Persisted promise, reused work, acknowledgement/link, execution progress, blocker, supported final report duplicate hints, and cancellation before acknowledgement/queued execution passed using deterministic agent decisions |

Validation evidence and the local package feed are under `artifacts/delegation-validation`.
`python scripts/verify-delegation-release.py` repeats the package-only release checks with a fresh
package cache, synchronizes the feed through dependency order, and verifies package/release versions.
`TrackedDelegationPostgresTests` creates a uniquely named disposable database on an explicitly
configured loopback server and deletes it afterward. It does not migrate the application database.
The three broader skips are existing PersonalTodo PostgreSQL, scoped-memory-audience, and board
workflow cases; the feature-specific suite has no skips.

`WorkTrackingApiTests` also provides an opt-in loopback browser fixture through
`CSWEET_DELEGATION_BROWSER_FIXTURE`. The available browser tool rejected both localhost and
127.0.0.1 with `ERR_BLOCKED_BY_CLIENT`; Chrome was unavailable. Browser interaction was therefore
not verified. The rendered component and authenticated HTTP API were verified separately. A live
Naomi/Gabriel journey with configured providers and reviewed installation grants remains rollout
acceptance, beyond the deterministic SDK/platform test. No packages were published and no live
employee, conversation or application database was modified.
