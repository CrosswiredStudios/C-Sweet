# Current Activity

The Overview (`CommandCenter`) includes a reorderable **Current Activity** widget.
The widget occupies one-third of the grid at viewport widths of 1400px and above,
half at 1001-1399px, and full width on narrower screens. The neighboring **Needs your
attention** widget combines Decisions, Approvals, and Hiring with keyboard-accessible
tabs. Its panels remain mounted to preserve drafts and expansion state. Settings →
Appearance switches Overview and the Projects portfolio between the Aurora gradient
and Black & Gold. `AppearanceState` persists the shared preference in browser storage;
the Overview header no longer contains its own appearance picker.
`app.css` removes the overview's outer container gutter and the hidden desktop
toolbar offset, so the background reaches the viewport edges beside navigation.
The shared navigation uses dark plum and lavender accents, switching to charcoal
and gold with the overview's Black & Gold theme. Cards retain their inner spacing.
The Current Activity widget keeps a fixed 560px outer height across loading, empty,
and populated states, with its header and footer outside the scrolling list. Other
widgets sharing a grid row stretch to the tallest widget. The compact page header
omits reorder guidance, save confirmations, reset, and manual refresh; existing
realtime and reconnect subscriptions keep data current. Failed updates retain a retry action.
`DashboardWidgets.Restore` merges legacy action queues at the first queue's saved
position, preserving the relative order of the other widgets and appending missing activity.

## Projection and scope

`CurrentActivityService.ReadAsync` combines active inbox deliveries, canonical
running/waiting/blocked work items, and model calls running outside an inbox
attempt. `WorkExecutionContext` supplies the attempt's current task focus;
`WorkStageExecution` and claim-event links supply explicit fallback attribution.
Current stage assignees take precedence over a ticket's accountable/direct owner.
Unlinked model calls are labelled background activity, never guessed onto an
agent's most recently edited ticket. Parallel executors remain separate rows.

`CurrentActivityService.ReadDeliveriesAsync` also resolves `WorkDeliveryStage`
inbox deliveries through the exact `WorkExecutionAttempt.AgentWorkItemId` binding,
stage source ID, and assigned employee/installation. Aggregate epic and release
reviews show their canonical stage and plan scope (for example, **Technical review**
and **Release: First playable**) even when no task is linked. These rows participate
in the Projects filter and require the delivery board's read grant; feed reads
recheck that grant, including release reviews with no task ID. Missing, foreign,
archived, or mismatched delivery bindings never fall back to unrestricted background
activity. An independently leased review does not inherit the source epic's wait.

`DescribeActivity` supplies allowlisted labels for technical/acceptance reviews,
build readiness, testing, project-health reviews, task discussions, and known
delivery/compute/repository notifications. `CurrentActivityItem.ActivityLabel`
keeps the assigned activity visible alongside the runtime status. Unknown events
retain the generic fallback; no label is inferred from provider reasoning. Recorded
workspace operations and agent activity/progress take priority over model status
in the current-action summary. Queued, recovering, and unconfirmed activity retains
its state and never displays an active reviewer or tester.

The organization-scoped list is paged in groups of 50 after authorization and
deduplication. Confirmed executing work sorts first, followed by project category,
creation time and stable row key. `ReadExecutionAsync` resolves provider queues and
stale progress for potentially executing candidates before pagination, reusing those
snapshots during rendering. Full provider stream bodies remain on demand. The client
reapplies execution priority across the full list as leases and progress age.
`CurrentActivity.RefreshAsync` automatically follows the authorized 50-item pages
to the end and deduplicates row keys before replacing the displayed list. Filters,
organization changes, and disposal cancel stale page reads. The full list is always
available by scrolling, with no Show all, Show fewer, or Load more work action.
`ActivityCard` uses readable headings for
collaboration, review, Communications, model calls, and task work. Linked task titles
open the project's Work tab with `board` and `item` query parameters when linked
to a project, or the standalone board otherwise. Personal tasks use the authorized board owner's
employee page with `tab=personal-board&item=...`, including the diagnostic panel's
Open work item link. `EmployeePersonalBoard.RequestedItemId` opens only a ticket
present in the authorized board response and preserves drafts across refreshes.
`ActivityAgent` displays the existing employee
portrait, name, role, and a labelled active state only while execution is confirmed.
Both the portrait and name link to employee details. Collaboration headings use
the authorized, sanitized session subject, falling back to the linked task title
or “Agent coordination.” Project links use the authorized task board's WorkstreamId;
missing associations never produce guessed project links. The active participant
has a pulsing circle behind their portrait; reduced motion and Pause animation
retain a static highlight. Waiting, recovering and unconfirmed rows do not pulse.
`ActivityCard` places the current action or “What's needed” beside the agent identity
in a compact summary row; needs-attention rows retain their attention label. Narrow
cards stack the summary using container queries, including collaboration layouts.
Waiting cards emphasize the reported dependency or queue reason. Cards show recent
workspace operations with timestamps; generic model-call chatter stays out of this
compact history. Technical details disclosures have been removed. Peek at provider
stream opens the existing authorized feed inline and fetches only on demand.
No synthetic progress or percentage completion is generated.

For coordination deliveries, `CurrentActivityService` binds the two participants and
source task through `AgentCoordinationSession.CurrentAgentWorkItemId`, the current
speaker, and the participant installation. Session-source tasks go through the same
board authorization as execution-context tasks, including feed reads and rejection
of omitted task IDs. A collaborator is shown only when their employee diagnostics
are also authorized. Stale or foreign sessions do not supply identities or task links.
Protected inbox payloads and conversation history are not read to construct cards.
An ongoing collaboration uses its speaker's delivery lease to establish execution,
even when the source task is blocked or awaiting approval; a source dependency
must not hide independent collaboration.

`Executing` requires a live claimed attempt or a recorded active standalone model
call. Waiting for claim, review, or explicit dependencies is separate from
execution. Expired leases are `Recovering`; five minutes without task progress
is `Unconfirmed`. Heartbeat renewal does not prove task progress. Display ages
and lease expiry update locally without network polling.

`ReadExecutionAsync` includes the exact attempt/task's provider start and completion
timestamps in freshness. It also considers timestamp-only pending model audit receipts
(excluding the initial source snapshot), so delayed ledger sealing cannot hide an
actively streaming call. It never reads another attempt or task's receipts or exposes
pending protected content. `CurrentActivityFeedReader.ProgressText` accepts SDK
`activity.started` and `activity.completed` deltas, including inference-state reports.

## Reasoning and actions

`CurrentActivityFeedReader` projects existing audit evidence into timestamped
provider reasoning, model output, tool requests, tool results, and agent progress.
Tool requests describe model intent; only recorded results describe returned
tool evidence. Private/protected reasoning is never decrypted for presentation,
and unavailable reasoning is stated explicitly rather than fabricated.

Attempt feeds select model records by organization, installation, attempt, and
exact work item. Task progress must explicitly identify the selected item.
Standalone feeds select one authorized model run. Neither endpoint searches an
employee's recent events by time as a substitute for execution correlation.

Audit payloads are unprotected using the existing audit keys, checked against
their evidence hash and seal, and projected through an allowlist of readable
fields with the shared redaction policy. Invalid or missing evidence produces
an availability notice. Existing producers continue saving their audit outbox
records atomically with source changes; this feature adds no parallel history.

Each page reads at most 150 audit receipts. Initial reads show the most recent
receipts and identify omitted earlier content; subsequent reads advance a
durable ledger watermark. The client deduplicates event IDs and stream keys,
orders model fragments by their provider stream sequence, and combines fragments
into one entry per response kind. The client retains at most 1,000 fragments and
32,000 displayed characters per combined entry, with explicit truncation notices.
The employee timeline retains access to older evidence.

The inline stream supports expand, follow latest, manual refresh, Escape within
the widget, explicit dismissal, and focus restoration. Clicking elsewhere keeps
the stream open. Scrolling back pauses following without stopping capture.
The stream closes when its card leaves the current list. Plain Razor text rendering
keeps model/tool content inert.

## Authorization and updates

Every read requires an active human organization member. Standard/personal board
read grants control task visibility. `EmployeeAuditAccess` separately controls
diagnostics and unlinked agent activity. Feed requests reauthorize both the
employee and the selected task; omitting a known task ID cannot bypass board
authorization. Diagnostic reads are themselves audited.

The widget listens to the existing audit, work-board, and employee-directory
realtime hints, coalesces bursts, and re-reads authoritative REST state. It also
reconciles on connect/reconnect. Empty employee audit hints, including the
widget's own diagnostic-access audit, do not cause a refresh loop. Denied or
missing feed reads clear previously shown evidence. Switching organizations,
filters, or attempts cancels stale requests.

`CSweetDbContext.QueueCurrentActivityWake` commits content-free
`AppRealtimeEvents.CurrentActivityChanged` hints with model lifecycle, progress and
stream audit source receipts. Streaming hints are coalesced to at most one per five
seconds per organization in the streaming context; lifecycle transitions remain
immediate. `ApplicationRealtimeOutboxDispatcher.ResolveRecipientsAsync` rechecks
employee diagnostic access for these hints just as for sealed audit notifications.
The widget rereads authorized state; a hint is neither sealed evidence nor an
execution grant. `AuditOutboxWorker.DrainAsync` drains full 100-record batches with
fresh scopes before returning to its idle timer, preserving retry backoff and all
receipts instead of limiting throughput to 100 records per ten seconds.

October 20 diagnosis: Victor Lin's technical-planning provider call completed at
21:18:26 Pacific and another began at 21:18:41 in the same live Gabriel/Victor
collaboration. Model receipts were present, but over 108,000 model audit receipts
were awaiting delivery. Missing SDK activity-delta recognition and ignoring provider
lifecycle/pending receipt timestamps allowed the older callback to appear unconfirmed.
The platform and frontend require rebuilding/restarting to activate these changes;
no agent package update or database migration is required.

## Code and verification

- `CSweet.Contracts/Core/CurrentActivityContracts.cs`: list, item, step, and feed DTOs.
- `CSweet.Api/Core/CompanyDashboardEndpoints`: activity list, attempt feed, standalone model feed.
- `CSweet.Infrastructure/Core/CurrentActivityService`: discovery, scope, authorization, state.
- `CSweet.Infrastructure/Core/CurrentActivityFeedReader`: evidence projection and watermark paging.
- `CSweet.UI/Components/CurrentActivity`: card list, inline provider stream, event reconciliation, fragment grouping.
- `CSweet.UI/Components/ActivityCard`, `ActivityAgent`: activity-specific card layouts and employee identities.
- `CSweet.UI/wwwroot/currentActivity.js`: scroll following, dismissal, focus restoration.
- `CurrentActivityTests`, `CurrentActivityRenderingTests`, and `CompanyDashboard*Tests`:
  task/attempt isolation, diagnostic and board grants, standalone calls, pagination,
  redaction, replay/out-of-order fragments, waiting/recovery, encoding, and layout compatibility.

No database migration is required. Restart/rebuild the API and web frontend to load
the feature. Existing uncorrelated historical model records cannot be retroactively
attributed to a task.

### Local workspace operations

Video Game Engineer 4.0.3 and Software Developer 2.0.2 use `WorkspaceProgress` in assigned development to emit
`kind: workspace-tool` progress before/after file operations and confined shell
execution. These are actual tool invocation reports, not model intentions. Shell
results include exit codes and timeouts; operation counters and consecutive identical
command counts make repeated attempts visible without asserting that repetition is
necessarily a loop. File-tool returns are labelled as returns, not successful checks.
Only fixed labels and counters are reported: no paths, commands, file content, or
shell output. A telemetry failure does not replay a tool mutation.

`CurrentActivityFeedReader` validates each audit receipt and reads progress from its
attempt-bound, protected `AgentWorkProgress` source. This works with content-free
audit receipts and does not recreate erased content from audit history. Up to 150
recent progress records are considered; workspace summaries take precedence over
model-call chatter, and `ActivityCard` shows two timestamped recent operations.
Normal task and employee-diagnostic authorization still applies.

The inbox has a 4,096-record bound shared by model and local tool progress. The
agent reports at most 500 local operations (start/result pairs), then emits an
explicit reporting-limit notice and continues work. This leaves room for its
bounded model continuations rather than exhausting the inbox with telemetry.

The reporting agent package must be rebuilt/upgraded as well as the API, AgentHost,
and UI (including the expanded bounded progress budget). An
already running VM retains its old agent code; do not restart it to gain telemetry
while it has uncheckpointed work. New telemetry cannot reconstruct old local tools.

`scripts/Test-DeveloperAgentCoreParity.ps1` checks the two agents' execution source and
harness/progress regression tests for drift. Their role profiles and versions remain distinct.
