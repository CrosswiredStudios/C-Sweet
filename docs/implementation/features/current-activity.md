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
Widgets sharing a grid row stretch to the tallest widget. The compact page header
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

The organization-scoped list is paged in groups of 50 after authorization and
deduplication. Confirmed executing work sorts first, followed by project category,
creation time and stable row key. `ReadExecutionAsync` resolves provider queues and
stale progress for potentially executing candidates before pagination, reusing those
snapshots during rendering. Full provider stream bodies remain on demand. The client
reapplies execution priority before its three-card preview as leases and progress age.
The widget initially shows three activity cards; Show all opens a bounded scrolling
list, with Load more for additional pages. `ActivityCard` uses readable headings for
collaboration, review, Communications, model calls, and task work. Linked task titles
open the board's `item` deep link. Personal tasks use the authorized board owner's
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
Waiting cards emphasize the reported dependency or queue reason. Recent progress
and Technical details disclosures have been removed from cards. Peek at provider
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

No database migration or external agent package change is required. Restart/rebuild
the API and web frontend to load the feature. Existing uncorrelated historical
model records cannot be retroactively attributed to a task.
