# Universal work management

## Product decision

C-Sweet has one canonical application-wide work item. Every work item belongs to
exactly one operational board and has one workflow state. Cross-board searches,
portfolio dashboards, reports, and saved views may display items from several
boards, but they do not create additional board membership or state.

Every organization receives a default `To Do -> Done` board so that existing and
new application tasks always have a home. Moving an item to another board is an
explicit, grant-secured transfer rather than a generic field update.

## Board directory and management

The organization Work area provides a board directory that:

- lists only boards covered by the current subject's explicit read grants;
- supports search, workstream filtering, favorites, recent boards, and archived
  board management;
- identifies the default board and shows active item counts, active sprint
  context when available, and the number of subjects with access;
- allows personal favorites under read access and separately granted create,
  configure, archive, and restore actions;
- exposes the people, agents, and automation identities with board access from
  board configuration;
- provides links to board, list, backlog, sprint, activity, automation, and
  access-management views.

Archive is the normal destructive operation. It preserves work items, activity,
grants, reports, and security evidence.

## Delivery phases

### Phase 1 - secure board foundation

- Persist boards, columns, board preferences, and generic scoped action grants.
- Provision one default board and backfill existing tasks into it.
- Backfill explicit board grants from legacy organization permission levels once;
  subsequent authorization uses grants rather than the legacy level.
- Deliver the board directory API and shared web/MAUI UI.

### Phase 2 - canonical work items and workflows

- Generalize `WorkTask` to `WorkItem` while preserving IDs, task runs, artifacts,
  strategic objectives, and compatibility routes.
- Add configurable work types, workflows, transitions, WIP limits, ranking,
  hierarchy, relations, comments, transfers, realtime events, and MCP tools.

Implemented foundation: the canonical record now supports typed work items,
single-board column placement, parent hierarchy, stable ranking, revision-checked
movement, configurable column categories, warning/hard WIP policies, and distinct
move/complete/cancel/reopen grants. Agent MCP tools now support scoped board
discovery, board reads, board and typed-item creation, movement, completion,
cancellation, reopening, comments, and cross-board transfer. Comments and
workflow changes produce durable item activity. Human and agent mutations publish
grant-filtered realtime board events through the application outbox, and open
board/detail views refresh from those events. A transfer requires authority on
both boards, preserves the item's single canonical state, checks the target WIP
policy, and rejects partial movement of an existing hierarchy. Agent writes
require durable idempotency keys and the platform enforces both the installation
capability grant and the scoped board/action grant. Relations, comment
editing/deletion, mentions, and hierarchy-aware batch transfer remain in this
phase.

The companion `CSweet.Agent.SDK` 1.1 surface now registers the supported
`work.*` capabilities for manifest validation and exposes a typed
`context.Platform.Work` client for board discovery, canonical item lifecycle,
comments, estimates, transfers, sprints, reports, and automations. The SDK and
broker both reference the dependency-light `CSweet.WorkManagement.Contracts`
package for capability names and transport DTOs, avoiding duplicate .NET wire
models. Agents no longer need to construct raw MCP payloads for these workflows.

### Phase 3 - sprints and automation

- Add board-scoped sprints, goals, estimates, capacity, scope snapshots,
  carryover, Agile reports, and grant-secured event-condition-action automation.

Implemented sprint foundation: boards can hold planned, active, completed, and
cancelled sprints with goals and optional schedules. A database invariant permits
only one active sprint per board. A canonical work item has at most one current
sprint and uses no sprint as the backlog state; cross-board transfer clears that
membership. Humans and agents can list, create, start, complete, cancel, and
manage sprint scope through separate board-scoped grants. Sprint and scope
mutations are revision checked, idempotent, audited, and realtime published.
Story-point estimates, sprint capacity targets, immutable completion snapshots,
bulk carryover of incomplete work, and snapshot-based velocity/capacity reports
are now implemented for both human APIs and agent MCP tools under separate
grants. Durable scope/status metric points now drive per-sprint burndown history
and a conservative active-sprint forecast based on completed-sprint velocity;
both are available through the human and agent report surfaces.

Implemented automation foundation: humans and agents can create, inspect,
update, enable, disable, and (before first execution) delete board event rules.
Each rule has a dedicated automation identity that starts without authority and
must hold the exact scoped item-action grant when it runs. Execution outcomes,
including denials and WIP failures, are durable; successful moves carry the
authorizing grant ID and revision into item activity and the security ledger.
Automation-produced item events are not eligible as rule triggers, preventing
recursive rule loops. The first action type is a grant-secured move, complete,
cancel, or reopen into a configured column. Rich field conditions, notifications,
assignments, scheduled triggers, approvals, and bounded multi-step rule chains
remain later automation extensions.

## Security invariants

- Humans, contractors, agents, and automation identities use the same explicit
  scoped action-grant model.
- Every read and mutation resolves an action and resource scope before data is
  returned or changed.
- Grant management is itself grant secured. Delegation may only issue a
  delegable subset of the issuer's authority.
- Agent board-access profiles never imply package capabilities, and package
  capabilities never imply board access; both gates must pass.
- Mutations require idempotency and optimistic concurrency as their resources
  acquire revisions.
- Allowed and denied operations retain the authorizing grant revision in the
  security audit trail.
