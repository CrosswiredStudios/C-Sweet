# Current Activity

The Overview (`CommandCenter`) includes a reorderable **Current Activity** widget.
New layouts place it after approvals. `DashboardWidgets.Restore` appends missing
widgets to supported older layouts without changing the saved relative order.

## Projection and scope

`CurrentActivityService.ReadAsync` combines active inbox deliveries, canonical
running/waiting/blocked work items, and model calls running outside an inbox
attempt. `WorkExecutionContext` supplies the attempt's current task focus;
`WorkStageExecution` and claim-event links supply explicit fallback attribution.
Current stage assignees take precedence over a ticket's accountable/direct owner.
Unlinked model calls are labelled background activity, never guessed onto an
agent's most recently edited ticket. Parallel executors remain separate rows.

The organization-scoped list is paged in groups of 50 after authorization and
deduplication. Project work sorts first, then creation time and stable row key.
The compact widget initially shows three rows; Show all opens a bounded scrolling
list, with Load more for additional pages. Each row displays up to two earlier
reported progress/model phases and the current state. No synthetic progress or
percentage completion is generated.

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

The popover supports expand, follow latest, manual refresh, Escape, outside-click
dismissal, and focus restoration. Scrolling back pauses following without
stopping capture. Narrow screens use an inset panel. Plain Razor text rendering
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
- `CSweet.UI/Components/CurrentActivity`: Signal rows, popover, event reconciliation, fragment grouping.
- `CSweet.UI/wwwroot/currentActivity.js`: scroll following, dismissal, focus restoration.
- `CurrentActivityTests`, `CurrentActivityRenderingTests`, and `CompanyDashboard*Tests`:
  task/attempt isolation, diagnostic and board grants, standalone calls, pagination,
  redaction, replay/out-of-order fragments, waiting/recovery, encoding, and layout compatibility.

No database migration or external agent package change is required. Restart/rebuild
the API and web frontend to load the feature. Existing uncorrelated historical
model records cannot be retroactively attributed to a task.
