# Universal work management

## Code map

- `src/CSweet.Domain/Core/WorkTask.cs` - canonical work-item entity. Named `WorkTask`
  to avoid conflict with `System.Threading.Tasks.Task`; docs use "work item" for the
  product concept. Carries `BoardId`, `BoardColumnId`, `SprintId`, `ParentWorkTaskId`,
  `BoardRank`, `TypeKey`, `PlanningRevision`, `AssignmentRevision`, `Revision`,
  `StructuredMentionsJson`, planning/delivery/estimate JSON, approvals, and
  `Dependencies`/`Dependents` (`WorkItemDependency`).
- `src/CSweet.Domain/WorkManagement/WorkBoard.cs` - `WorkBoard`, `WorkBoardColumn`,
  `WorkBoardUserPreference`, `WorkSprint`, `WorkItemDependency`, `WorkQualityRun`,
  `WorkSprintSnapshot`, `WorkSprintMetricPoint`, `WorkSprintMutationReceipt`,
  `WorkItemMutationReceipt`, `WorkItemComment`, `WorkItemActivity`, `WorkItemApproval`.
- `src/CSweet.Domain/WorkManagement/WorkOrchestration.cs` - policy-pinned sprint
  execution: `WorkOrchestrationPolicy`, revisions, stages, transitions, sprint/item/stage
  executions, attempts, and orchestration events. Normative lifecycle is
  `Documentation/Architecture/CSWEET_BOARD_ORCHESTRATION_SPEC.md`.
- `src/CSweet.Application/WorkManagement/` - `IWorkBoardService`,
  `IWorkBoardGrantService`, `IWorkBoardBehavior`, `IWorkItemCollaborationService`
  (comments, activity, transfer), `IWorkSprintService`, `IWorkOrchestrationService`,
  `IPersonalTodoService`/`IWorkItemMutationEngine` (personal boards).
- `src/CSweet.Infrastructure/WorkManagement/` - `WorkBoardService`,
  `WorkBoardProvisioning` (default board, legacy-grant backfill, task placement),
  `WorkBoardGrantService`, `WorkBoardBehaviors` (`StandardBoardBehavior`,
  `HumanPersonalBoardBehavior`, `AgentPersonalBoardBehavior`),
  `WorkItemCollaborationService`, `WorkItemMentionCodec`, `WorkSprintService`,
  `WorkSprintReportBuilder`, `WorkSprintSnapshotFactory`, `WorkSprintMetricsRecorder`,
  `WorkOrchestrationService`, `WorkOrchestrator`, `WorkItemMutationEngine`
  (in `PersonalTodoService.cs`), `PersonalWorkPlanning`, `PersonalTodoActivityReader`.
- `src/CSweet.Infrastructure/Persistence/WorkManagementConfigurations.cs` - EF mappings
  and invariants: unique board key per organization, one default board per
  organization, one active sprint per board, unique sprint sequence, revision
  concurrency tokens, idempotency-key uniqueness, no-self-dependency check.
- `src/CSweet.Api/WorkManagement/WorkBoardEndpoints.cs` - `MapWorkBoardEndpoints`:
  board directory/detail CRUD, favorites, columns, items, moves, collaboration,
  transfer, sprints, capacity, carryover, sprint reports, grants, orchestration,
  delivery-pipeline assignment, and the `personal-todos` group.
- `src/CSweet.AgentHost/Broker/WorkManagementCapabilityHandler.cs` - agent `work.*`
  broker entrypoint. `PersonalTodoCapabilityHandler` covers personal-todo actions.
- `src/CSweet.Contracts/WorkManagement/WorkBoardContracts.cs` - human-facing DTOs and
  action constants: `WorkBoardActions`, `WorkItemActions`, `WorkSprintActions`,
  `WorkAutomationActions` (contract/UI surface only), `PersonalTodoActions`.
- `src/CSweet.UI/Pages/WorkBoards.razor` - board directory, detail workspace,
  sprints, orchestration, delivery pipeline, grants, comments/activity, transfer,
  and automation dialogs. Detail components live in
  `src/CSweet.UI/Components/WorkBoards/`; personal boards use
  `src/CSweet.UI/Components/Employees/EmployeePersonalBoard.razor`.
- Wire contracts shared with agents live in the sibling
  `CSweet.WorkManagement.Contracts` package (`work.*` capability names and DTOs).

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

- Generalize `WorkTask` to the canonical `WorkItem` concept while preserving IDs, task runs, artifacts,
  strategic objectives, and compatibility routes. The code entity remains `WorkTask`
  (`src/CSweet.Domain/Core/WorkTask.cs`, named to avoid conflict with `System.Threading.Tasks.Task`).
- Add configurable work types, workflows, transitions, WIP limits, ranking,
  hierarchy, relations, comments, transfers, realtime events, and MCP tools.

Implemented foundation: the canonical record now supports typed work items,
single-board column placement, parent hierarchy, stable ranking, revision-checked
movement, configurable column categories, warning/hard WIP policies, and distinct
move/complete/cancel/reopen grants. Structured mentions are implemented through
`WorkItemMentionCodec` (title/description spans persisted in
`WorkTask.StructuredMentionsJson`); item dependencies are persisted as
`WorkItemDependency` rows with a no-self-dependency invariant. Agent MCP tools now support scoped board
discovery, board reads, board and typed-item creation, movement, completion,
cancellation, reopening, comments, and cross-board transfer. Comments and
workflow changes produce durable item activity. Human and agent mutations publish
grant-filtered realtime board events through the application outbox, and open
board/detail views refresh from those events. A transfer requires authority on
both boards, preserves the item's single canonical state, clears sprint
membership, reissues the `{BoardKey}-{Sequence}` identifier on the target board,
checks the target WIP policy, and rejects hierarchical items unless they are
detached or moved with their hierarchy. Agent writes
require durable idempotency keys and the platform enforces both the installation
capability grant and the scoped board/action grant. Hierarchy-aware batch transfer
remains unimplemented. Comment editing and deletion
now ship as their own feature: see [work item comments](../work-item-comments.md),
which also extends comments to personal boards.

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

Implemented automation status: the `AddBoardWorkOrchestration` migration dropped
the `WorkAutomationRules` and `WorkAutomationExecutions` tables in favor of
policy-pinned board orchestration (see `Documentation/Architecture/CSWEET_BOARD_ORCHESTRATION_SPEC.md`).
The automation contract/UI surface still exists (`WorkAutomationActions`,
`WorkAutomationRuleResponse`, `WorkAutomationDirectoryResponse`,
`CreateWorkAutomationRuleRequest`, `UpdateWorkAutomationRuleRequest` in
`src/CSweet.Contracts/WorkManagement/WorkBoardContracts.cs`, plus the automations
dialog in `src/CSweet.UI/Pages/WorkBoards.razor`), but there is currently no
API or infrastructure backend behind it. Do not document event rules as available
until a backend is reintroduced. Rich field conditions, notifications,
assignments, scheduled triggers, approvals, and bounded multi-step rule chains
remain later automation extensions.

## Personal boards

Personal boards (`WorkBoardKind.Personal`) are per-owner boards managed by
`IPersonalTodoService`/`IWorkItemMutationEngine` (`WorkItemMutationEngine` in
`src/CSweet.Infrastructure/WorkManagement/PersonalTodoService.cs`) and exposed
through the `personal-todos` route group in `WorkBoardEndpoints`. Board behavior
is selected by `IWorkBoardBehavior`: `StandardBoardBehavior` for team boards,
`HumanPersonalBoardBehavior` (owner can create and transition directly, no claim
lease), and `AgentPersonalBoardBehavior` (owner can create, transitions require
the claim lease). Personal boards authorize with the `personal-todo` action
family, not `work.item.*`, except for human-owner comment actions and transfer.
Managers keep read/reorder/requeue visibility over their reporting chain but
receive no comment authority; agent-owned personal boards receive no comment
grants. Reconciliation (`ReconcileAsync`, plus `PersonalTodoReconciliationWorker`)
ensures boards for active owners, revokes grants for inactive owners, expires
claims, retries eligible work, and enforces soft/hard open-item limits.

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
