# Ticket-defined project workflows

`WorkItemPlanningSpecification.Workflow` owns the execution declaration for a `WorkItemTypeKeys.WorkflowTicketV1` ticket. Requirements, acceptance criteria and `DependencyItemIds` use ordinary canonical work-item planning. A review exists on the board before execution starts.

`WorkDeliveryService.ConfigureAsync` requires `TicketWorkflows` matching those canonical declarations. It snapshots planning revisions, evidence inputs, assignments and source bindings. It does not infer review stages from Story, Epic or Release labels, require those containers, or insert QA, technical or manager acceptance. A flat ticket graph is valid. Source-scope labels are explicit input and branch bindings.

`WorkDeliveryTicketStep.Key` identifies an agent-chosen step. `Handler` selects execution mechanics: `review`, `source-review`, `quality`, `build-readiness`, `compute-test`, `acceptance`, or mechanical `completion`. The declared order is authoritative. Agents can split assessments into smaller tickets with separate criteria and dependencies. Choosing a quality operation retains that operation's evidence and independence checks; it does not require other tickets to use it.

`OrderedTicketExecutions` follows declared predecessors. `TicketPredecessorsCompleteAsync` reads tracked canonical state, allowing successors to advance in the same atomic reconciliation as predecessor completion. Rejected work holds its dependents while other ready tickets continue. Completion, promotion and container updates follow the explicit declaration. Default-branch promotion honors declared final-release dependencies without inferring gates from item titles.

`SynchronizeTicketAsync` updates the canonical ticket's status, assignee, waiting reason and blocker, with its board notification outbox record in the same transaction. `CurrentActivityService.ReadDeliveriesAsync` links active execution to the ticket. Events are wake hints; authorized current-state reads, revision checks and idempotency receipts determine progress after duplicate notifications or restarts.

SDK `TicketWorkflowPlanning.ConfigureAsync` creates real board tickets before configuring execution. `HierarchicalProjectDelivery` is one agent planning recipe: it declares mechanical source/evidence preparation and the selected increment acceptance steps. `WorkDeliveryBranchBinding.TargetBaseBranch` explicitly requests initial integration-target creation. The platform does not infer a special branch name.

Activated graphs are immutable; follow-up work uses a new graph. No adoption path retrofits legacy running reviews. Broader existing task-amendment and recovery APIs are retained rather than removed in this refactor. The discarded game's processes are not a compatibility target or a prerequisite for fresh-business validation.

## Verification

- `TicketWorkflowTests`: flat readiness, custom ordering, rejection isolation, changed criteria, duplicate/restart behavior, and the real SDK → board service → delivery service path.
- `HierarchicalDeliveryServiceTests`: an explicitly authored example plan, exact candidates, quality operations, cross-board/repository authorization, retained source receipts, and cancellation.
- Producer `ProgressiveSprintHandoffTests.Accepted_increment_recovers_planning_and_starts_successor_once_across_restart_and_duplicate_wakes`: acceptance discovery, planning continuation, successor refinement and Sprint 2 activation through agent callbacks.
- SDK `IncrementDeliveryPlanningTests` and `SingleQaStaffingTests`: explicit source initialization, ticket creation, staffing and draft retries.

Fresh businesses use Producer 4.0.0, Technical Director 5.0.1 and QA 5.0.1, with SDK 5.0.2 and Work Management Contracts 5.0.1. Developer task execution is unchanged.
